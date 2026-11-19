using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

public sealed partial class HttpDownloader
{
    /// <summary>Honest name used when a site shows browsers a web page but gives download tools the file.</summary>
    public const string ToolUserAgent = "GolanDownloadManager/2 (Windows; download manager)";

    const int MaxInterstitialHtmlBytes = 512 * 1024;
    const int MaxHtmlInterstitialHops = 3;

    static readonly string[] WebPageExtensions = [".htm", ".html", ".xhtml", ".shtml", ".mht", ".mhtml", ".php", ".asp", ".aspx", ".jsp"];
    static readonly string[] ExplicitHtmlExtensions = [".htm", ".html", ".xhtml", ".shtml", ".mht", ".mhtml"];
    static readonly string[] IntentionalHtmlExtensions = [".htm", ".html", ".xhtml", ".mht"];

    static readonly TimeSpan HtmlRxTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex StripNonVisibleHtmlRx = new(
        @"<!--[\s\S]*?-->|<(?:script|style|noscript)\b[^>]*>[\s\S]*?</(?:script|style|noscript)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, HtmlRxTimeout);
    static readonly Regex HtmlFormBlockRx = new(
        @"<form\b([^>]*)>([\s\S]*?)</form>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, HtmlRxTimeout);
    static readonly Regex HtmlInputRx = new(
        @"<input\b([^>]*)/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, HtmlRxTimeout);
    static readonly Regex HtmlMetaTagRx = new(
        @"<meta\b([^>]*)/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, HtmlRxTimeout);
    static readonly Regex HtmlAnchorTagRx = new(
        @"<a\b([^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, HtmlRxTimeout);
    static readonly Regex HtmlAttrRx = new(
        @"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+)))?",
        RegexOptions.Compiled, HtmlRxTimeout);

    /// <summary>True when the server sent a web page although a real file (setup, archive, video...) was expected.</summary>
    public static bool IsPageInsteadOfFile(string? mime, string? expectedName)
    {
        if (string.IsNullOrEmpty(mime) || !mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(expectedName ?? "");
        return ext.Length > 1 && !WebPageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the response is an HTML page without Content-Disposition: attachment and the user did not explicitly request an HTML document.
    /// Catches extensionless download endpoints (/download?id=..., /uc?id=...) returning confirm or login/error pages.
    /// </summary>
    public static bool IsUnexpectedHtmlResponse(HttpResponseMessage resp, string? mime, string? expectedName)
    {
        if (string.IsNullOrEmpty(mime)) return false;
        bool isHtml = mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                   || mime.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
        if (!isHtml) return false;
        if (HasAttachmentDisposition(resp)) return false;
        var ext = Path.GetExtension(expectedName ?? "");
        if (ext.Length > 1 && ExplicitHtmlExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return false;
        return true;
    }

    internal static bool IsHtmlFileName(string? name)
        => Path.GetExtension(name ?? "").ToLowerInvariant() is ".htm" or ".html" or ".xhtml" or ".mht";

    internal static bool IsHtmlWebPageMime(string? mime)
        => !string.IsNullOrEmpty(mime)
           && (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
               || mime.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True only when the user manually requested the web page itself (not from browser capture, sharing-host resolver, or queue sync)
    /// and the URL path or user-locked file name ends in .htm, .html, .xhtml, or .mht.
    /// </summary>
    public static bool IsIntentionalWebPageDownload(DownloadItem item, Uri origUri, bool usingPreResolved = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(origUri);

        if (!item.AddedManually || item.TriggeredBySync)
            return false;
        if (usingPreResolved || !string.IsNullOrWhiteSpace(item.ResolvedUrl))
            return false;
        if (Options.FindQueue(item.QueueId) is { IsSync: true } or { Periodic: true })
            return false;
        if (Resolvers.HostResolvers.All.Any(r => r.CanHandle(origUri)))
            return false;

        if (item.NameLocked && !string.IsNullOrWhiteSpace(item.FileName))
        {
            var lockedExt = Path.GetExtension(item.FileName);
            return lockedExt.Length > 1 && IntentionalHtmlExtensions.Contains(lockedExt, StringComparer.OrdinalIgnoreCase);
        }

        var urlExt = Path.GetExtension(origUri.AbsolutePath);
        return urlExt.Length > 1 && IntentionalHtmlExtensions.Contains(urlExt, StringComparer.OrdinalIgnoreCase);
    }

    static bool HasAttachmentDisposition(HttpResponseMessage resp)
    {
        if (string.Equals(resp.Content.Headers.ContentDisposition?.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase))
            return true;
        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var rawValues))
        {
            foreach (var raw in rawValues)
            {
                var first = raw.Split(';')[0].Trim();
                if (first.Equals("attachment", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    static async Task<string> ReadBoundedHtmlAsync(HttpResponseMessage resp, CancellationToken ct, int maxBytes = MaxInterstitialHtmlBytes)
    {
        if (resp.Content.Headers.ContentLength is long cl && cl > maxBytes)
            return "";
        var rawStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var stream = WrapDecompressionStream(rawStream, resp, decompressGzipTargets: true);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int total = 0;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (total < maxBytes)
        {
            int n = await ReadWithTimeoutAsync(stream, buf, Math.Min(buf.Length, maxBytes - total), ct, timeoutCts).ConfigureAwait(false);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            total += n;
        }
        var enc = Encoding.UTF8;
        var cs = resp.Content.Headers.ContentType?.CharSet?.Trim('"', '\'', ' ');
        if (!string.IsNullOrWhiteSpace(cs))
        {
            try { enc = Encoding.GetEncoding(cs); }
            catch (ArgumentException) { /* fall back to UTF-8 */ }
        }
        return enc.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Extracts a followable same-site confirm form, meta-refresh, or download-button target from an interstitial HTML page.</summary>
    public static bool TryExtractHtmlDownloadTarget(string html, Uri pageUri, Uri origUri, out Uri? target, out string? suggestedFileName)
    {
        target = null;
        suggestedFileName = null;
        if (string.IsNullOrWhiteSpace(html)) return false;

        suggestedFileName = Resolvers.GoogleDriveResolver.ExtractHtmlFileName(html);

        // 1. Official Google Drive confirm form helper (allows drive.google.com -> drive.usercontent.google.com).
        if (Resolvers.GoogleDriveResolver.ParseConfirmFormHtml(html, pageUri) is { } gdUri
            && IsAllowedRedirectTarget(origUri, gdUri)
            && IsAllowedRedirectTarget(pageUri, gdUri))
        {
            target = gdUri;
            return true;
        }

        string cleaned;
        try { cleaned = StripNonVisibleHtmlRx.Replace(html, " "); }
        catch (RegexMatchTimeoutException) { return false; }

        // 2. Generic same-site GET confirm/download <form>.
        try
        {
            foreach (Match fm in HtmlFormBlockRx.Matches(cleaned))
            {
                var attrs = ParseHtmlAttrs(fm.Groups[1].Value);
                if (attrs.TryGetValue("method", out var method) && !string.IsNullOrWhiteSpace(method)
                    && !method.Equals("get", StringComparison.OrdinalIgnoreCase))
                    continue;

                var formId = attrs.GetValueOrDefault("id", "");
                var formAction = attrs.GetValueOrDefault("action", "");
                var inputs = new List<(string Key, string Val)>();
                bool hasConfirmInput = false;

                foreach (Match im in HtmlInputRx.Matches(fm.Groups[2].Value))
                {
                    var ia = ParseHtmlAttrs(im.Groups[1].Value);
                    var type = ia.GetValueOrDefault("type", "text").ToLowerInvariant();
                    if (type is "button" or "reset" or "image" or "file") continue;
                    if (type is "checkbox" or "radio" && !ia.ContainsKey("checked")) continue;
                    if (!ia.TryGetValue("name", out var iName) || string.IsNullOrWhiteSpace(iName)) continue;
                    var iVal = ia.GetValueOrDefault("value", "");
                    if (iName.Equals("confirm", StringComparison.OrdinalIgnoreCase)) hasConfirmInput = true;
                    inputs.Add((iName, iVal));
                }

                bool looksLikeConfirmForm = hasConfirmInput
                    || formId.Contains("download", StringComparison.OrdinalIgnoreCase)
                    || formId.Contains("confirm", StringComparison.OrdinalIgnoreCase)
                    || formAction.Contains("confirm=", StringComparison.OrdinalIgnoreCase);
                if (!looksLikeConfirmForm) continue;

                var actionBase = string.IsNullOrWhiteSpace(formAction) ? pageUri : ResolveHtmlHref(pageUri, formAction);
                if (actionBase is null) continue;

                var candidate = BuildGetFormUri(actionBase, inputs);
                if (IsAllowedInterstitialTarget(origUri, pageUri, candidate))
                {
                    target = candidate;
                    return true;
                }
            }

            // 3. <meta http-equiv="refresh" content="0; url=...">
            foreach (Match mm in HtmlMetaTagRx.Matches(cleaned))
            {
                var attrs = ParseHtmlAttrs(mm.Groups[1].Value);
                if (!attrs.TryGetValue("http-equiv", out var he) || !he.Equals("refresh", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!attrs.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content))
                    continue;
                if (TryParseMetaRefreshContent(content, pageUri, out var refreshUri)
                    && refreshUri != null
                    && IsAllowedInterstitialTarget(origUri, pageUri, refreshUri))
                {
                    target = refreshUri;
                    return true;
                }
            }

            // 4. Same-site explicit download/confirm link (<a id="downloadButton|uc-download-link" ...>, <a download ...>, or ?confirm=...).
            foreach (Match am in HtmlAnchorTagRx.Matches(cleaned))
            {
                var attrs = ParseHtmlAttrs(am.Groups[1].Value);
                if (!attrs.TryGetValue("href", out var href) || string.IsNullOrWhiteSpace(href))
                    continue;
                var id = attrs.GetValueOrDefault("id", "");
                var cls = attrs.GetValueOrDefault("class", "");
                if (attrs.TryGetValue("data-download-url", out var dataDl) && !string.IsNullOrWhiteSpace(dataDl))
                    href = dataDl;
                bool hasDownloadAttr = attrs.ContainsKey("download");
                var anchorUri = ResolveHtmlHref(pageUri, href);
                var anchorLeafExt = anchorUri != null ? Path.GetExtension(anchorUri.AbsolutePath) : "";
                bool pointsToBinaryExt = anchorLeafExt.Length > 1 && !WebPageExtensions.Contains(anchorLeafExt, StringComparer.OrdinalIgnoreCase);
                bool hasDownloadBtnClass = pointsToBinaryExt && (
                    cls.Contains("download-btn", StringComparison.OrdinalIgnoreCase)
                    || cls.Contains("download-button", StringComparison.OrdinalIgnoreCase)
                    || cls.Contains("btn-download", StringComparison.OrdinalIgnoreCase));
                bool isExplicitDownloadAnchor = hasDownloadAttr
                    || id.Equals("downloadButton", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("uc-download-link", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("direct-download", StringComparison.OrdinalIgnoreCase)
                    || href.Contains("confirm=", StringComparison.OrdinalIgnoreCase)
                    || hasDownloadBtnClass;
                if (!isExplicitDownloadAnchor) continue;

                if (anchorUri != null && IsAllowedInterstitialTarget(origUri, pageUri, anchorUri))
                {
                    target = anchorUri;
                    return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return false;
    }

    static bool IsAllowedInterstitialTarget(Uri origUri, Uri pageUri, Uri candidate)
        => IsAllowedRedirectTarget(origUri, candidate)
        && IsAllowedRedirectTarget(pageUri, candidate)
        && IsSameSiteHost(pageUri, candidate);

    static bool IsSameSiteHost(Uri a, Uri b)
    {
        var ha = a.DnsSafeHost.ToLowerInvariant();
        var hb = b.DnsSafeHost.ToLowerInvariant();
        if (ha == hb)
        {
            // For IP / localhost, require same port if non-default? Same host on loopback is same-site in local setups,
            // but block arbitrary port hopping on loopback.
            if ((IPAddress.TryParse(ha, out _) || ha == "localhost") && a.Port != b.Port)
                return false;
            return true;
        }
        if (IPAddress.TryParse(ha, out _) || IPAddress.TryParse(hb, out _))
            return false;
        if ((ha is "drive.google.com" or "docs.google.com") && hb == "drive.usercontent.google.com")
            return true;
        return GetRegistrableDomain(ha) is { Length: > 0 } da
            && da.Equals(GetRegistrableDomain(hb), StringComparison.OrdinalIgnoreCase);
    }

    static string GetRegistrableDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        // Handle common two-label public suffixes like .co.uk, .com.au, .co.nz
        if (parts.Length >= 3 && parts[^1].Length == 2 && parts[^2] is "co" or "com" or "org" or "net" or "gov" or "edu" or "ac")
            return $"{parts[^3]}.{parts[^2]}.{parts[^1]}";
        return $"{parts[^2]}.{parts[^1]}";
    }

    static Dictionary<string, string> ParseHtmlAttrs(string rawAttrs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in HtmlAttrRx.Matches(rawAttrs))
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Success ? m.Groups[2].Value
                    : m.Groups[3].Success ? m.Groups[3].Value
                    : m.Groups[4].Success ? m.Groups[4].Value
                    : "";
            dict[key] = WebUtility.HtmlDecode(val).Trim();
        }
        return dict;
    }

    static Uri? ResolveHtmlHref(Uri baseUri, string rawHref)
    {
        var cleaned = rawHref.Trim();
        if (cleaned.Length == 0
            || cleaned.StartsWith('#')
            || cleaned.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || cleaned.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || cleaned.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;
        return Uri.TryCreate(baseUri, cleaned, out var u) ? u : null;
    }

    static Uri BuildGetFormUri(Uri actionUri, List<(string Key, string Val)> inputs)
    {
        var merged = new List<(string Key, string Val)>();
        var overridden = new HashSet<string>(inputs.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        var existingQuery = actionUri.Query.TrimStart('?');
        if (!string.IsNullOrWhiteSpace(existingQuery))
        {
            foreach (var part in existingQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                var k = Uri.UnescapeDataString(eq >= 0 ? part[..eq] : part);
                if (overridden.Contains(k)) continue;
                var v = eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..]) : "";
                merged.Add((k, v));
            }
        }
        merged.AddRange(inputs);
        var sb = new StringBuilder();
        foreach (var (k, v) in merged)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return new UriBuilder(actionUri) { Query = sb.ToString() }.Uri;
    }

    static bool TryParseMetaRefreshContent(string content, Uri baseUri, out Uri? target)
    {
        target = null;
        int semi = content.IndexOf(';');
        if (semi < 0) return false;
        var delayStr = content[..semi].Trim();
        if (delayStr.Length > 0 && (!double.TryParse(delayStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double delay) || delay < 0 || delay > 15))
            return false;
        var rest = content[(semi + 1)..].Trim();
        if (rest.StartsWith("url", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[3..].TrimStart();
            if (!rest.StartsWith('=')) return false;
            rest = rest[1..].Trim().Trim('"', '\'').Trim();
        }
        else
        {
            return false;
        }
        target = ResolveHtmlHref(baseUri, rest);
        return target != null;
    }

    /// <summary>Anything private (cookies, a login) that must never travel over plain http.</summary>
    static bool CarriesSecrets(RequestContext ctx, Uri origUri)
        => !string.IsNullOrEmpty(ctx.Login) || !string.IsNullOrEmpty(ctx.Cookies(origUri, true)) || MatchSiteLogin(origUri) != null;

    /// <summary>Cloudflare-style "prove you are a browser" answers (managed challenge): never retried automatically.</summary>
    public static bool IsBrowserChallenge(HttpResponseMessage resp)
        => resp.Headers.TryGetValues("cf-mitigated", out var v) && v.Any(x => x.Contains("challenge", StringComparison.OrdinalIgnoreCase));
}
