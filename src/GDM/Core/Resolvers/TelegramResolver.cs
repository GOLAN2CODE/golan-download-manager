using System.Net;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core.Resolvers;

/// <summary>Resolves public Telegram post links (t.me/&lt;channel&gt;/&lt;id&gt;) by inspecting the public embed widget.</summary>
public sealed class TelegramResolver : IHostResolver
{
    static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex ChannelRx = new(@"^[a-zA-Z][a-zA-Z0-9_]{3,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex VideoSrcRx = new(@"<(?:video|audio|source)\b[^>]*\bsrc\s*=\s*[""'](https://[^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex DataVideoRx = new(@"\bdata-(?:video|audio|src)\s*=\s*[""'](https://[^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);
    static readonly Regex DocTitleRx = new(@"<div\b[^>]*\bclass\s*=\s*[""'][^""']*\btgme_widget_message_document_title\b[^""']*[""'][^>]*>([^<]+)</div>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);

    static readonly HashSet<string> ReservedChannelSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "s", "c", "joinchat", "addstickers", "addemoji", "addlist", "iv", "proxy", "socks", "share", "login",
    };

    public bool CanHandle(Uri u) => TryParsePostUrl(u, out _, out _);

    public static bool TryParsePostUrl(Uri u, out string channel, out string messageId)
    {
        channel = "";
        messageId = "";
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = u.Host.ToLowerInvariant();
        if (host is not ("t.me" or "www.t.me" or "telegram.me" or "www.telegram.me"))
            return false;

        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Handle both /<channel>/<id> and /s/<channel>/<id>
        if (segments.Length == 3 && string.Equals(segments[0], "s", StringComparison.OrdinalIgnoreCase))
            segments = segments[1..];

        if (segments.Length != 2) return false;
        if (ReservedChannelSlugs.Contains(segments[0]) || !ChannelRx.IsMatch(segments[0]))
            return false;
        if (!segments[1].All(char.IsAsciiDigit) || segments[1].Length is 0 or > 15)
            return false;

        channel = segments[0];
        messageId = segments[1];
        return true;
    }

    public async Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct)
    {
        if (!TryParsePostUrl(u, out var channel, out var messageId))
            return null;

        var embedUri = new Uri($"https://t.me/{channel}/{messageId}?embed=1&mode=tme");
        using var req = new HttpRequestMessage(HttpMethod.Get, embedUri);
        using var resp = await HostResolvers.SendFollowHttpsAsync(http, req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var html = await HostResolvers.ReadBoundedStringAsync(resp.Content, ct).ConfigureAwait(false);
        string? mediaUrl = null;
        var m = VideoSrcRx.Match(html);
        if (m.Success)
            mediaUrl = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
        else if (DataVideoRx.Match(html) is { Success: true } dm)
            mediaUrl = WebUtility.HtmlDecode(dm.Groups[1].Value.Trim());

        if (string.IsNullOrWhiteSpace(mediaUrl)
            || !Uri.TryCreate(mediaUrl, UriKind.Absolute, out var directUri)
            || !HostResolvers.IsSafePublicHttpsUri(directUri))
        {
            return null;
        }

        string? fileName = null;
        var titleMatch = DocTitleRx.Match(html);
        if (titleMatch.Success)
            fileName = HostResolvers.SanitizeDecodedFileName(WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()));

        if (string.IsNullOrWhiteSpace(fileName))
        {
            var ext = Path.GetExtension(directUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
            fileName = FileNames.Sanitize($"{channel}_{messageId}{ext}");
        }

        return new ResolvedLink(directUri, fileName, -1, HostResolvers.EmptyHeaders);
    }
}
