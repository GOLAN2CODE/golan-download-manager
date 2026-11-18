using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

public sealed class GitHubResolver : IHostResolver
{
    static readonly Regex RepoPartRx = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public bool CanHandle(Uri u) => RewriteBlobUrl(u) != null || TryParseReleasePage(u, out _, out _, out _);

    public static Uri? RewriteBlobUrl(Uri u)
    {
        if (u is null || !u.IsAbsoluteUri) return null;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("github.com" or "www.github.com")) return null;

        var path = u.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Expected: [0]=owner, [1]=repo, [2]="blob", [3]=ref, [4..]=file path
        if (segments.Length < 5) return null;
        if (!string.Equals(segments[2], "blob", StringComparison.Ordinal)) return null;

        foreach (var seg in segments)
        {
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(seg); }
            catch { return null; }
            if (unescaped is "." or ".." || unescaped.Contains('/') || unescaped.Contains('\\'))
                return null;
        }

        var owner = segments[0];
        var repo = segments[1];
        var gitRef = segments[3];
        var restPath = string.Join("/", segments.Skip(4));
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(gitRef) || string.IsNullOrWhiteSpace(restPath))
            return null;
        if (!RepoPartRx.IsMatch(owner) || !RepoPartRx.IsMatch(repo))
            return null;

        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{gitRef}/{restPath}";
        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var rewritten) ? rewritten : null;
    }

    public static bool TryParseReleasePage(Uri u, out string owner, out string repo, out string? tag)
    {
        owner = "";
        repo = "";
        tag = null;
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("github.com" or "www.github.com")) return false;

        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4) return false;
        if (!string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase)) return false;

        // Note: /releases/download/<tag>/<asset> is already a direct 302 asset URL handled natively by HttpDownloader.
        if (segments.Length == 4 && string.Equals(segments[3], "latest", StringComparison.OrdinalIgnoreCase))
        {
            owner = segments[0];
            repo = segments[1];
            return RepoPartRx.IsMatch(owner) && RepoPartRx.IsMatch(repo);
        }

        if (segments.Length == 5 && string.Equals(segments[3], "tag", StringComparison.OrdinalIgnoreCase))
        {
            owner = segments[0];
            repo = segments[1];
            try { tag = Uri.UnescapeDataString(segments[4]); }
            catch { return false; }
            if (string.IsNullOrWhiteSpace(tag) || tag is "." or ".." || tag.Contains('/') || tag.Contains('\\'))
                return false;
            return RepoPartRx.IsMatch(owner) && RepoPartRx.IsMatch(repo);
        }

        return false;
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        var rawUri = RewriteBlobUrl(u);
        if (rawUri != null)
        {
            var fileName = HostResolvers.SanitizeDecodedFileName(FileNames.FromUrl(rawUri.ToString()));
            return new ResolvedLink(rawUri, fileName, -1, HostResolvers.EmptyHeaders);
        }

        if (TryParseReleasePage(u, out var owner, out var repo, out var tag))
        {
            var apiPath = tag is null
                ? $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest"
                : $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/tags/{Uri.EscapeDataString(tag)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, apiPath);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await HostResolvers.SendFollowHttpsAsync(http, req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var jsonText = await HostResolvers.ReadBoundedStringAsync(resp.Content, ct).ConfigureAwait(false);
            JsonObject? root;
            try { root = JsonNode.Parse(jsonText) as JsonObject; }
            catch { return null; }
            if (root is null) return null;

            if (root["assets"] is JsonArray assets && assets.Count > 0)
            {
                var chosen = PickReleaseAsset(assets);
                if (chosen != null)
                {
                    var dlUrl = chosen["browser_download_url"]?.GetValue<string>();
                    var name = HostResolvers.SanitizeDecodedFileName(chosen["name"]?.GetValue<string>());
                    long size = chosen["size"]?.GetValue<long>() ?? -1;
                    if (!string.IsNullOrWhiteSpace(dlUrl)
                        && Uri.TryCreate(dlUrl, UriKind.Absolute, out var assetUri)
                        && HostResolvers.IsSafePublicHttpsUri(assetUri))
                    {
                        return new ResolvedLink(assetUri, name, size, HostResolvers.EmptyHeaders);
                    }
                }
            }

            var zipUrl = root["zipball_url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(zipUrl)
                && Uri.TryCreate(zipUrl, UriKind.Absolute, out var zipUri)
                && HostResolvers.IsSafePublicHttpsUri(zipUri))
            {
                var tagLabel = HostResolvers.SanitizeDecodedFileName(root["tag_name"]?.GetValue<string>()) ?? "release";
                return new ResolvedLink(zipUri, FileNames.Sanitize($"{repo}-{tagLabel}.zip"), -1, HostResolvers.EmptyHeaders);
            }
        }

        return null;
    }

    internal static JsonObject? PickReleaseAsset(JsonArray assets)
    {
        var objects = assets.OfType<JsonObject>().ToList();
        if (objects.Count == 0) return null;
        if (objects.Count == 1) return objects[0];

        static bool IsChecksumOrSig(string n)
            => n.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".sha512", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".sig", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".asc", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        var candidates = objects.Where(o => !IsChecksumOrSig(o["name"]?.GetValue<string>() ?? "")).ToList();
        if (candidates.Count == 0) candidates = objects;

        var winX64 = candidates.FirstOrDefault(o =>
        {
            var n = (o["name"]?.GetValue<string>() ?? "").ToLowerInvariant();
            // "darwin" (macOS) contains "win": take it out before looking for a Windows build.
            bool win = n.Replace("darwin", "", StringComparison.Ordinal).Contains("win", StringComparison.Ordinal)
                       || n.EndsWith(".exe", StringComparison.Ordinal) || n.EndsWith(".msi", StringComparison.Ordinal);
            bool arm = n.Contains("arm64", StringComparison.Ordinal) || n.Contains("aarch64", StringComparison.Ordinal);
            return win && !arm;
        });
        return winX64 ?? candidates[0];
    }
}
