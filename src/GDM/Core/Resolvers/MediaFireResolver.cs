using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

public sealed class MediaFireResolver : IHostResolver
{
    static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex ScriptBlockRx = new(@"<script\b[^>]*>[\s\S]*?</script\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AnchorTagRx = new(@"<a\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrIdDownloadBtnRx = new(@"\bid\s*=\s*[""']downloadButton[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrAriaDownloadRx = new(@"\baria-label\s*=\s*[""']Download file[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrScrambledRx = new(@"\bdata-scrambled-url\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex AttrHrefRx = new(@"\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);

    public bool CanHandle(Uri u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("www.mediafire.com" or "mediafire.com")) return false;

        return u.AbsolutePath.StartsWith("/file/", StringComparison.OrdinalIgnoreCase)
               || u.AbsolutePath.StartsWith("/file_premium/", StringComparison.OrdinalIgnoreCase)
               || u.AbsolutePath.StartsWith("/download/", StringComparison.OrdinalIgnoreCase);
    }

    public static Uri? ParseDownloadPageHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        try
        {
            var stripped = ScriptBlockRx.Replace(html, "");
            foreach (Match anchorMatch in AnchorTagRx.Matches(stripped))
            {
                var tag = anchorMatch.Value;
                if (!AttrIdDownloadBtnRx.IsMatch(tag) && !AttrAriaDownloadRx.IsMatch(tag))
                    continue;

                var scrambledMatch = AttrScrambledRx.Match(tag);
                if (scrambledMatch.Success)
                {
                    var b64 = WebUtility.HtmlDecode(scrambledMatch.Groups[1].Value.Trim());
                    if (TryDecodeBase64UrlString(b64, out var decodedUrl) && TryValidateMediaFireDirectUri(decodedUrl, out var scrambledUri))
                        return scrambledUri;
                }

                var hrefMatch = AttrHrefRx.Match(tag);
                if (hrefMatch.Success)
                {
                    var href = WebUtility.HtmlDecode(hrefMatch.Groups[1].Value.Trim());
                    if (TryValidateMediaFireDirectUri(href, out var directUri))
                        return directUri;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!CanHandle(u)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, u);
        using var resp = await HostResolvers.SendFollowHttpsAsync(http, req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
            && resp.RequestMessage?.RequestUri is { } directFinal
            && TryValidateMediaFireDirectUri(directFinal.ToString(), out _))
        {
            var (fn, sz) = HostResolvers.ReadResponseMeta(resp, directFinal);
            return new ResolvedLink(directFinal, fn, sz, HostResolvers.EmptyHeaders);
        }

        var html = await HostResolvers.ReadBoundedStringAsync(resp.Content, ct).ConfigureAwait(false);
        var extracted = ParseDownloadPageHtml(html);
        if (extracted is null) return null;

        var fileName = HostResolvers.SanitizeDecodedFileName(FileNames.FromUrl(extracted.ToString()));
        return new ResolvedLink(extracted, fileName, -1, HostResolvers.EmptyHeaders);
    }

    static bool TryDecodeBase64UrlString(string b64, out string decoded)
    {
        decoded = "";
        if (string.IsNullOrWhiteSpace(b64)) return false;
        try
        {
            var normalized = b64.Replace('-', '+').Replace('_', '/');
            int mod = normalized.Length % 4;
            if (mod == 2) normalized += "==";
            else if (mod == 3) normalized += "=";
            else if (mod == 1) return false;

            var bytes = Convert.FromBase64String(normalized);
            decoded = Encoding.UTF8.GetString(bytes).Trim();
            return !string.IsNullOrWhiteSpace(decoded);
        }
        catch
        {
            return false;
        }
    }

    static bool TryValidateMediaFireDirectUri(string rawUrl, out Uri validUri)
    {
        validUri = null!;
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) return false;
        if (!HostResolvers.IsSafePublicHttpsUri(uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host != "mediafire.com" && !host.EndsWith(".mediafire.com", StringComparison.Ordinal))
            return false;

        validUri = uri;
        return true;
    }
}
