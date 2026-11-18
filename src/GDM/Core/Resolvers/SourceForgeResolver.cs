using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

/// <summary>Rewrites SourceForge "/projects/&lt;project&gt;/files/&lt;path&gt;/download" HTML countdown pages to direct mirror-selector URLs.</summary>
public sealed class SourceForgeResolver : IHostResolver
{
    static readonly Regex ProjectRx = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public bool CanHandle(Uri u) => TryRewriteDownloadUrl(u, out _, out _);

    public static bool TryRewriteDownloadUrl(Uri u, out Uri? directUri, out string? fileName)
    {
        directUri = null;
        fileName = null;
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("sourceforge.net" or "www.sourceforge.net" or "sf.net"))
            return false;

        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Expected: /projects/<project>/files/<path...>/download
        if (segments.Length < 5) return false;
        if (!string.Equals(segments[0], "projects", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(segments[2], "files", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(segments[^1], "download", StringComparison.OrdinalIgnoreCase)) return false;

        var project = segments[1];
        if (!ProjectRx.IsMatch(project)) return false;

        var fileSegments = segments[3..^1];
        foreach (var seg in fileSegments)
        {
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(seg); }
            catch { return false; }
            if (unescaped is "." or ".." || unescaped.Contains('/') || unescaped.Contains('\\'))
                return false;
        }

        var joinedPath = string.Join("/", fileSegments);
        var leafRaw = Uri.UnescapeDataString(fileSegments[^1]);
        fileName = HostResolvers.SanitizeDecodedFileName(leafRaw);
        var rawTarget = $"https://downloads.sourceforge.net/project/{project}/{joinedPath}?use_mirror=autoselect";
        return Uri.TryCreate(rawTarget, UriKind.Absolute, out directUri);
    }

    public Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!TryRewriteDownloadUrl(u, out var directUri, out var fileName) || directUri is null)
            return Task.FromResult<ResolvedLink?>(null);

        return Task.FromResult<ResolvedLink?>(new ResolvedLink(directUri, fileName, -1, HostResolvers.EmptyHeaders));
    }
}
