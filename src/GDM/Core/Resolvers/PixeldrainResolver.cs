using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

/// <summary>Resolves Pixeldrain single-file sharing pages (/u/&lt;id&gt;) to direct API download URLs and rejects multi-file lists (/l/&lt;id&gt;).</summary>
public sealed class PixeldrainResolver : IHostResolver
{
    static readonly Regex IdRx = new(@"^[a-zA-Z0-9_-]{4,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public bool CanHandle(Uri u) => TryParsePixeldrain(u, out _, out bool isList) || isList;

    public static bool TryParsePixeldrain(Uri u, out string fileId, out bool isList)
    {
        fileId = "";
        isList = false;
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("pixeldrain.com" or "www.pixeldrain.com")) return false;

        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) return false;

        if (string.Equals(segments[0], "l", StringComparison.OrdinalIgnoreCase) && IdRx.IsMatch(segments[1]))
        {
            isList = true;
            return false;
        }

        if (string.Equals(segments[0], "u", StringComparison.OrdinalIgnoreCase) && IdRx.IsMatch(segments[1]))
        {
            fileId = segments[1];
            return true;
        }

        return false;
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!TryParsePixeldrain(u, out var fileId, out bool isList))
        {
            if (isList)
                throw new DownloadException(Loc.T("err.resolver.unsupported")) { NonFallback = true };
            return null;
        }

        var directUri = new Uri($"https://pixeldrain.com/api/file/{Uri.EscapeDataString(fileId)}?download");
        string? fileName = null;
        long size = -1;

        try
        {
            var infoUri = new Uri($"https://pixeldrain.com/api/file/{Uri.EscapeDataString(fileId)}/info");
            using var req = new HttpRequestMessage(HttpMethod.Get, infoUri);
            using var resp = await HostResolvers.SendFollowHttpsAsync(http, req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await HostResolvers.ReadBoundedStringAsync(resp.Content, ct).ConfigureAwait(false);
                if (JsonNode.Parse(json) is JsonObject obj)
                {
                    fileName = HostResolvers.SanitizeDecodedFileName(obj["name"]?.GetValue<string>());
                    size = obj["size"]?.GetValue<long>() ?? -1;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            /* metadata lookup is optional; direct /api/file/<id>?download still works */
        }

        return new ResolvedLink(directUri, fileName, size, HostResolvers.EmptyHeaders);
    }
}
