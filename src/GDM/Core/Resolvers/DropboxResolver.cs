using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Golan.Gdm.Core.Resolvers;

public sealed class DropboxResolver : IHostResolver
{
    public bool CanHandle(Uri u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("www.dropbox.com" or "dropbox.com")) return false;

        return u.AbsolutePath.StartsWith("/s/", StringComparison.OrdinalIgnoreCase)
               || u.AbsolutePath.StartsWith("/scl/", StringComparison.OrdinalIgnoreCase);
    }

    public static Uri RewriteUrl(Uri u)
    {
        ArgumentNullException.ThrowIfNull(u);

        var query = u.Query.TrimStart('?');
        var parts = string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split('&', StringSplitOptions.RemoveEmptyEntries);

        var sb = new StringBuilder();
        bool replacedDl = false;

        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            var key = eq >= 0 ? part[..eq] : part;
            if (string.Equals(key, "dl", StringComparison.OrdinalIgnoreCase))
            {
                if (!replacedDl)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append("dl=1");
                    replacedDl = true;
                }
                continue;
            }

            if (sb.Length > 0) sb.Append('&');
            sb.Append(part);
        }

        if (!replacedDl)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append("dl=1");
        }

        var builder = new UriBuilder(u)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
            Query = sb.ToString(),
        };
        return builder.Uri;
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!CanHandle(u)) return null;

        var directUri = RewriteUrl(u);
        using var headReq = new HttpRequestMessage(HttpMethod.Head, directUri);
        using var resp = await HostResolvers.SendFollowHttpsAsync(http, headReq, ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
        {
            using var getReq = new HttpRequestMessage(HttpMethod.Get, directUri);
            getReq.Headers.Range = new RangeHeaderValue(0, 0);
            using var getResp = await HostResolvers.SendFollowHttpsAsync(http, getReq, ct).ConfigureAwait(false);
            if (!getResp.IsSuccessStatusCode) return null;
            var finalGet = getResp.RequestMessage?.RequestUri ?? directUri;
            var (fnGet, szGet) = HostResolvers.ReadResponseMeta(getResp, u);
            return new ResolvedLink(finalGet, fnGet, szGet, HostResolvers.EmptyHeaders);
        }

        if (!resp.IsSuccessStatusCode) return null;

        var finalUri = resp.RequestMessage?.RequestUri ?? directUri;
        var (fileName, size) = HostResolvers.ReadResponseMeta(resp, u);
        return new ResolvedLink(finalUri, fileName, size, HostResolvers.EmptyHeaders);
    }
}
