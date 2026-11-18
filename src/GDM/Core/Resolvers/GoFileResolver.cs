using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

/// <summary>Resolves GoFile public sharing links (gofile.io/d/&lt;id&gt;) using a guest account token.</summary>
public sealed class GoFileResolver : IHostResolver
{
    const string DefaultWebsiteToken = "4fd6sg89d7s6";
    static readonly Regex ContentIdRx = new(@"^[a-zA-Z0-9_-]{3,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public bool CanHandle(Uri u) => TryExtractContentId(u, out _);

    public static bool TryExtractContentId(Uri u, out string contentId)
    {
        contentId = "";
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("gofile.io" or "www.gofile.io")) return false;

        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !string.Equals(segments[0], "d", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!ContentIdRx.IsMatch(segments[1]))
            return false;

        contentId = segments[1];
        return true;
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!TryExtractContentId(u, out var contentId))
            return null;

        using var acctReq = new HttpRequestMessage(HttpMethod.Post, "https://api.gofile.io/accounts")
        {
            Content = new ByteArrayContent([]),
        };
        using var acctResp = await HostResolvers.SendFollowHttpsAsync(http, acctReq, ct).ConfigureAwait(false);
        if (!acctResp.IsSuccessStatusCode) return null;

        var acctJson = await HostResolvers.ReadBoundedStringAsync(acctResp.Content, ct).ConfigureAwait(false);
        string? token = (JsonNode.Parse(acctJson) as JsonObject)?["data"]?["token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token) || token.Contains(';') || token.Contains('\r') || token.Contains('\n'))
            return null;

        var contentsUri = new Uri($"https://api.gofile.io/contents/{Uri.EscapeDataString(contentId)}?wt={DefaultWebsiteToken}");
        using var contentReq = new HttpRequestMessage(HttpMethod.Get, contentsUri);
        contentReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var contentResp = await HostResolvers.SendFollowHttpsAsync(http, contentReq, ct).ConfigureAwait(false);
        if (!contentResp.IsSuccessStatusCode) return null;

        var contentJson = await HostResolvers.ReadBoundedStringAsync(contentResp.Content, ct).ConfigureAwait(false);
        var root = JsonNode.Parse(contentJson) as JsonObject;
        if (!string.Equals(root?["status"]?.GetValue<string>(), "ok", StringComparison.OrdinalIgnoreCase))
            return null;

        var children = root?["data"]?["children"] as JsonObject;
        if (children is null || children.Count == 0)
            return null;

        var files = new List<JsonObject>();
        int folderCount = 0;
        foreach (var kv in children)
        {
            if (kv.Value is not JsonObject child) continue;
            var type = child["type"]?.GetValue<string>() ?? "";
            if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
                folderCount++;
            else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                files.Add(child);
        }

        if (files.Count == 0)
        {
            if (folderCount > 0)
                throw new DownloadException(Loc.T("err.resolver.unsupported")) { NonFallback = true };
            return null;
        }
        if (files.Count > 1)
            throw new DownloadException(Loc.T("err.resolver.unsupported")) { NonFallback = true };

        var file = files[0];
        var linkStr = file["link"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(linkStr)
            || !Uri.TryCreate(linkStr, UriKind.Absolute, out var directUri)
            || !HostResolvers.IsSafePublicHttpsUri(directUri))
        {
            return null;
        }

        var fileName = HostResolvers.SanitizeDecodedFileName(file["name"]?.GetValue<string>());
        long size = file["size"]?.GetValue<long>() ?? -1;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = "accountToken=" + token,
        };
        return new ResolvedLink(directUri, fileName, size, headers);
    }
}
