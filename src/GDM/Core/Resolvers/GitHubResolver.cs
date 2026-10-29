using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

public sealed class GitHubResolver : IHostResolver
{
    static readonly Regex RepoPartRx = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public bool CanHandle(Uri u) => RewriteBlobUrl(u) != null;

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

    public Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        var rawUri = RewriteBlobUrl(u);
        if (rawUri is null) return Task.FromResult<ResolvedLink?>(null);

        var fileName = HostResolvers.SanitizeDecodedFileName(FileNames.FromUrl(rawUri.ToString()));
        var link = new ResolvedLink(rawUri, fileName, -1, HostResolvers.EmptyHeaders);
        return Task.FromResult<ResolvedLink?>(link);
    }
}
