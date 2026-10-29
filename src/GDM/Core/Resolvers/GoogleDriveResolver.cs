using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

public sealed class GoogleDriveResolver : IHostResolver
{
    static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex FilePathRx = new(@"/file/d/([a-zA-Z0-9_-]{6,128})(?:/|$|\?)", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex FolderPathRx = new(@"/drive/(?:u/\d+/)?folders/([a-zA-Z0-9_-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex ScriptBlockRx = new(@"<script\b[^>]*>[\s\S]*?</script\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex FormTagBlockRx = new(@"<form\b([^>]*)>([\s\S]*?)</form\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrIdDownloadFormRx = new(@"\bid\s*=\s*[""']download-form[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrActionRx = new(@"\baction\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex InputTagRx = new(@"<input\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrNameRx = new(@"\bname\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrValueRx = new(@"\bvalue\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex UcFileNameRx = new(@"<(?:span|p)\b[^>]*\bclass\s*=\s*[""'][^""']*\buc-name-size\b[^""']*[""'][^>]*>\s*<a\b[^>]*>([^<]+)</a>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);

    static readonly HashSet<string> AllowedFormKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "export",
        "confirm",
        "uuid",
    };

    public bool CanHandle(Uri u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        bool ok = TryExtractFileId(u, out _, out bool isFolder);
        return ok || isFolder;
    }

    public static bool TryExtractFileId(Uri u, out string fileId, out bool isFolder)
    {
        fileId = "";
        isFolder = false;
        if (u is null || !u.IsAbsoluteUri) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("drive.google.com" or "docs.google.com" or "drive.usercontent.google.com"))
            return false;

        if (FolderPathRx.IsMatch(u.AbsolutePath))
        {
            isFolder = true;
            return false;
        }

        var pathMatch = FilePathRx.Match(u.AbsolutePath);
        if (pathMatch.Success)
        {
            fileId = pathMatch.Groups[1].Value;
            return true;
        }

        if (u.AbsolutePath.StartsWith("/open", StringComparison.OrdinalIgnoreCase)
            || u.AbsolutePath.StartsWith("/uc", StringComparison.OrdinalIgnoreCase)
            || u.AbsolutePath.StartsWith("/download", StringComparison.OrdinalIgnoreCase))
        {
            var idParam = GetQueryParam(u.Query, "id");
            if (!string.IsNullOrWhiteSpace(idParam) && IsValidDriveId(idParam))
            {
                fileId = idParam;
                return true;
            }
        }

        return false;
    }

    public static Uri? ParseConfirmFormHtml(string html, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(html) || baseUri is null) return null;

        try
        {
            var stripped = ScriptBlockRx.Replace(html, "");
            foreach (Match formMatch in FormTagBlockRx.Matches(stripped))
            {
                var formAttrs = formMatch.Groups[1].Value;
                var formBody = formMatch.Groups[2].Value;
                if (!AttrIdDownloadFormRx.IsMatch(formAttrs)) continue;

                var actionMatch = AttrActionRx.Match(formAttrs);
                if (!actionMatch.Success) continue;

                var rawAction = WebUtility.HtmlDecode(actionMatch.Groups[1].Value.Trim());
                if (!Uri.TryCreate(baseUri, rawAction, out var actionUri)) continue;
                if (!HostResolvers.IsSafePublicHttpsUri(actionUri)) continue;

                var actionHost = actionUri.Host.ToLowerInvariant();
                if (actionHost is not ("drive.usercontent.google.com" or "drive.google.com"))
                    continue;

                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match inputMatch in InputTagRx.Matches(formBody))
                {
                    var tag = inputMatch.Value;
                    var nameMatch = AttrNameRx.Match(tag);
                    if (!nameMatch.Success) continue;
                    var key = WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());
                    if (!AllowedFormKeys.Contains(key)) continue;

                    var valMatch = AttrValueRx.Match(tag);
                    var val = valMatch.Success ? WebUtility.HtmlDecode(valMatch.Groups[1].Value.Trim()) : "";
                    parameters[key] = val;
                }

                if (!parameters.TryGetValue("id", out var idVal) || !IsValidDriveId(idVal))
                    continue;

                var orderedKeys = new[] { "id", "export", "confirm", "uuid" };
                var sb = new StringBuilder();
                foreach (var k in orderedKeys)
                {
                    if (!parameters.TryGetValue(k, out var v) || string.IsNullOrEmpty(v)) continue;
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
                }

                var builder = new UriBuilder(actionUri) { Query = sb.ToString() };
                return builder.Uri;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    public static string? ExtractHtmlFileName(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        try
        {
            var stripped = ScriptBlockRx.Replace(html, "");
            var match = UcFileNameRx.Match(stripped);
            if (!match.Success) return null;
            var decoded = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            return HostResolvers.SanitizeDecodedFileName(decoded);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!TryExtractFileId(u, out var fileId, out bool isFolder))
        {
            if (isFolder)
                throw new DownloadException(Loc.T("err.resolver.unsupported"));
            return null;
        }

        var directUri = new Uri($"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download");
        using var req = new HttpRequestMessage(HttpMethod.Get, directUri);
        using var resp = await HostResolvers.SendFollowHttpsAsync(http, req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            var html = await HostResolvers.ReadBoundedStringAsync(resp.Content, ct).ConfigureAwait(false);
            var confirmedUri = ParseConfirmFormHtml(html, directUri);
            if (confirmedUri is null) return null;
            var htmlFileName = ExtractHtmlFileName(html);
            return new ResolvedLink(confirmedUri, htmlFileName, -1, HostResolvers.EmptyHeaders);
        }

        var finalUri = resp.RequestMessage?.RequestUri ?? directUri;
        var (fileName, size) = HostResolvers.ReadResponseMeta(resp, finalUri);
        return new ResolvedLink(finalUri, fileName, size, HostResolvers.EmptyHeaders);
    }

    static bool IsValidDriveId(string id)
    {
        if (id.Length is < 6 or > 128) return false;
        foreach (char c in id)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }

    static string? GetQueryParam(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = Uri.UnescapeDataString(part[..eq]);
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }
}
