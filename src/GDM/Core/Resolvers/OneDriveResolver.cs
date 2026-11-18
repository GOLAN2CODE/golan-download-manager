using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Golan.Gdm.Core.Resolvers;

public sealed class OneDriveResolver : IHostResolver
{
    public bool CanHandle(Uri u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is "1drv.ms" or "onedrive.live.com") return true;
        if (host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static string EncodeSharingToken(Uri u)
    {
        ArgumentNullException.ThrowIfNull(u);
        var bytes = Encoding.UTF8.GetBytes(u.ToString());
        var b64Url = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "u!" + b64Url;
    }

    public static Uri RewriteUrl(Uri u)
    {
        ArgumentNullException.ThrowIfNull(u);

        var host = u.Host.ToLowerInvariant();
        if (host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = u.Query.TrimStart('?');
            var parts = string.IsNullOrWhiteSpace(query)
                ? []
                : query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            var sb = new StringBuilder();
            bool hasDownload = false;
            foreach (var part in parts)
            {
                int eq = part.IndexOf('=');
                var key = eq >= 0 ? part[..eq] : part;
                if (string.Equals(key, "download", StringComparison.OrdinalIgnoreCase))
                {
                    if (!hasDownload)
                    {
                        if (sb.Length > 0) sb.Append('&');
                        sb.Append("download=1");
                        hasDownload = true;
                    }
                    continue;
                }
                if (sb.Length > 0) sb.Append('&');
                sb.Append(part);
            }

            if (!hasDownload)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append("download=1");
            }

            var builder = new UriBuilder(u)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1,
                Query = sb.ToString(),
            };
            return builder.Uri;
        }

        var token = EncodeSharingToken(u);
        return new Uri($"https://api.onedrive.com/v1.0/shares/{token}/root/content");
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!CanHandle(u)) return null;

        var rewritten = RewriteUrl(u);
        using var req = new HttpRequestMessage(HttpMethod.Get, rewritten);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await HostResolvers.SendFollowHttpsAsync(http, req, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable
            or HttpStatusCode.BadRequest
            or HttpStatusCode.Forbidden
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented)
        {
            using var plainReq = new HttpRequestMessage(HttpMethod.Get, rewritten);
            using var plainResp = await HostResolvers.SendFollowHttpsAsync(http, plainReq, ct).ConfigureAwait(false);
            if (!plainResp.IsSuccessStatusCode) return null;
            var finalPlain = plainResp.RequestMessage?.RequestUri ?? rewritten;
            var (fnPlain, szPlain) = HostResolvers.ReadResponseMeta(plainResp, finalPlain);
            return new ResolvedLink(finalPlain, fnPlain, szPlain, HostResolvers.EmptyHeaders);
        }
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
            && !u.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            var directDownloadUri = AppendDownloadQuery(u);
            using var fbReq = new HttpRequestMessage(HttpMethod.Get, directDownloadUri);
            using var fbResp = await HostResolvers.SendFollowHttpsAsync(http, fbReq, ct).ConfigureAwait(false);
            if (!fbResp.IsSuccessStatusCode) return null;
            var fbMime = fbResp.Content.Headers.ContentType?.MediaType ?? "";
            if (string.Equals(fbMime, "text/html", StringComparison.OrdinalIgnoreCase)) return null;
            var fbFinal = fbResp.RequestMessage?.RequestUri ?? directDownloadUri;
            var (fbName, fbSize) = HostResolvers.ReadResponseMeta(fbResp, fbFinal);
            return new ResolvedLink(fbFinal, fbName, fbSize, HostResolvers.EmptyHeaders);
        }
        if (!resp.IsSuccessStatusCode) return null;

        var finalUri = resp.RequestMessage?.RequestUri ?? rewritten;
        var (fileName, size) = HostResolvers.ReadResponseMeta(resp, finalUri);
        return new ResolvedLink(finalUri, fileName, size, HostResolvers.EmptyHeaders);
    }

    static Uri AppendDownloadQuery(Uri u)
    {
        var q = u.Query.TrimStart('?');
        var nextQ = string.IsNullOrEmpty(q) ? "download=1" : q + "&download=1";
        return new UriBuilder(u) { Scheme = Uri.UriSchemeHttps, Port = -1, Query = nextQ }.Uri;
    }
}
