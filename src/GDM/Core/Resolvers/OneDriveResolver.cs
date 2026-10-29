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
        if (!resp.IsSuccessStatusCode) return null;

        var finalUri = resp.RequestMessage?.RequestUri ?? rewritten;
        var (fileName, size) = HostResolvers.ReadResponseMeta(resp, finalUri);
        return new ResolvedLink(finalUri, fileName, size, HostResolvers.EmptyHeaders);
    }
}
