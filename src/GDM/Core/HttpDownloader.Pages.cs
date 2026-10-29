namespace Golan.Gdm.Core;

public sealed partial class HttpDownloader
{
    /// <summary>Honest name used when a site shows browsers a web page but gives download tools the file.</summary>
    public const string ToolUserAgent = "GolanDownloadManager/2 (Windows; download manager)";

    static readonly string[] WebPageExtensions = [".htm", ".html", ".xhtml", ".shtml", ".mht", ".mhtml", ".php", ".asp", ".aspx", ".jsp"];

    /// <summary>True when the server sent a web page although a real file (setup, archive, video...) was expected.</summary>
    public static bool IsPageInsteadOfFile(string? mime, string? expectedName)
    {
        if (string.IsNullOrEmpty(mime) || !mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(expectedName ?? "");
        return ext.Length > 1 && !WebPageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Anything private (cookies, a login) that must never travel over plain http.</summary>
    static bool CarriesSecrets(RequestContext ctx, Uri origUri)
        => !string.IsNullOrEmpty(ctx.Login) || !string.IsNullOrEmpty(ctx.Cookies(origUri, true)) || MatchSiteLogin(origUri) != null;

    /// <summary>Cloudflare-style "prove you are a browser" answers (managed challenge): never retried automatically.</summary>
    public static bool IsBrowserChallenge(HttpResponseMessage resp)
        => resp.Headers.TryGetValues("cf-mitigated", out var v) && v.Any(x => x.Contains("challenge", StringComparison.OrdinalIgnoreCase));
}
