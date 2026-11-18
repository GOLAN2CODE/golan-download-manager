using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Golan.Gdm.Core;

public sealed partial class HttpDownloader
{
    /// <summary>Applies proxy and other connection settings; safe to call while downloads run.</summary>
    public static void Configure(AppSettings settings)
    {
        Options = settings;
        Client = Build(settings);
        Integrity.Configure(settings);
        DownloadItem.TempRoot = string.IsNullOrWhiteSpace(settings.TempDir) ? "" : settings.TempDir;
    }

    /// <summary>Builds a WebProxy.BypassList regex that matches scheme://host(:port)(/path) without prefix/suffix collisions.</summary>
    public static string? BypassRegexForPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var p = pattern.Trim().ToLowerInvariant();
        if (p.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(p, UriKind.Absolute, out var u)) p = u.Host;
        int slash = p.IndexOf('/');
        if (slash >= 0) p = p[..slash];
        int colon = p.IndexOf(':');
        string? portPart = null;
        if (colon >= 0)
        {
            portPart = p[(colon + 1)..];
            p = p[..colon];
        }
        if (string.IsNullOrWhiteSpace(p)) return null;
        var portRx = !string.IsNullOrEmpty(portPart) && portPart.All(char.IsDigit) ? $":{portPart}" : @"(?::\d+)?";

        if (p.StartsWith("*."))
        {
            var suffix = p[2..];
            if (string.IsNullOrWhiteSpace(suffix) || suffix.Contains('*') || suffix.Contains('?')) return null;
            return @"^(?:https?://)?(?:[^/:]+\.)+" + System.Text.RegularExpressions.Regex.Escape(suffix) + portRx + @"(?:/.*)?$";
        }
        if (p.Contains('*') || p.Contains('?'))
        {
            if (p.EndsWith(".*") && !p[..^2].Contains('*') && !p[..^2].Contains('?') && p[..^2].All(c => char.IsDigit(c) || c == '.'))
                return @"^(?:https?://)?" + System.Text.RegularExpressions.Regex.Escape(p[..^2]) + @"\.[0-9.]+" + portRx + @"(?:/.*)?$";
            return null;
        }
        if (IPAddress.TryParse(p, out _))
            return @"^(?:https?://)?" + System.Text.RegularExpressions.Regex.Escape(p) + portRx + @"(?:/.*)?$";
        return @"^(?:https?://)?(?:[^/:]+\.)?" + System.Text.RegularExpressions.Regex.Escape(p) + portRx + @"(?:/.*)?$";
    }

    static WebProxy BuildWebProxy(AppSettings s)
    {
        var scheme = s.ProxyType is "socks5" or "socks4" or "https" ? s.ProxyType : "http";
        var proxy = new WebProxy(new Uri($"{scheme}://{s.ProxyHost}:{s.ProxyPort}"))
        {
            BypassProxyOnLocal = false,
            BypassList = Wildcard.SplitList(s.ProxyBypass)
                .Select(BypassRegexForPattern)
                .Where(x => x != null)
                .Select(x => x!)
                .ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(s.ProxyUser)) proxy.Credentials = new NetworkCredential(s.ProxyUser, s.ProxyPassword);
        return proxy;
    }

    /// <summary>True when <paramref name="urlOrHost"/> matches the manual proxy bypass list (or when no proxy is used).</summary>
    public static bool IsProxyBypassed(string? urlOrHost, AppSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return false;
        var trimmed = urlOrHost.Trim();
        if (!Uri.TryCreate(trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "http://" + trimmed, UriKind.Absolute, out var uri))
            return false;
        return IsProxyBypassed(uri, settings);
    }

    public static bool IsProxyBypassed(Uri uri, AppSettings? settings = null)
    {
        var s = settings ?? Options;
        if (s.ProxyMode == "none") return true;
        if (s.ProxyMode != "manual" || string.IsNullOrWhiteSpace(s.ProxyHost)) return false;
        return BuildWebProxy(s).IsBypassed(uri);
    }

    internal static void ApplyProxy(SocketsHttpHandler handler, AppSettings s)
    {
        switch (s.ProxyMode)
        {
            case "none":
                handler.UseProxy = false;
                break;
            case "manual" when !string.IsNullOrWhiteSpace(s.ProxyHost):
                handler.Proxy = BuildWebProxy(s);
                handler.UseProxy = true;
                break;
            default:
                handler.UseProxy = true; // system proxy
                break;
        }
    }

    static HttpClient Build(AppSettings s)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false, // redirects are followed by hand so cookies never leak to other hosts
            UseCookies = false,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        ApplyProxy(handler, s);
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Proxy argument for yt-dlp, or null to use the system default. Returns "" when bypassed or disabled.</summary>
    public static string? ProxyForTools(string? url = null)
    {
        var s = Options;
        if (s.ProxyMode == "none") return "";
        if (s.ProxyMode != "manual" || string.IsNullOrWhiteSpace(s.ProxyHost)) return null;
        if (!string.IsNullOrWhiteSpace(url) && IsProxyBypassed(url, s)) return "";
        var auth = string.IsNullOrWhiteSpace(s.ProxyUser) ? "" : $"{Uri.EscapeDataString(s.ProxyUser)}:{Uri.EscapeDataString(s.ProxyPassword)}@";
        var scheme = s.ProxyType is "socks5" or "socks4" or "https" ? s.ProxyType : "http";
        return $"{scheme}://{auth}{s.ProxyHost}:{s.ProxyPort}";
    }

    sealed record RequestContext(Uri? OriginalUri, string CookieHost, string Referrer, string UserAgent, Func<Uri, bool, string> Cookies, string Login, string Password);

    static RequestContext ContextFor(DownloadItem item) => new(
        Uri.TryCreate(item.Url, UriKind.Absolute, out var orig) ? orig : null,
        item.CookieHost.Trim(),
        item.Referrer,
        item.UserAgent,
        (uri, sameHostAndSecure) => CookiesForRequest(item, uri, sameHostAndSecure),
        item.Login,
        item.Password);

    /// <summary>Host-only, downgrade-safe cookie lookup for <paramref name="item"/> when requesting <paramref name="uri"/>.</summary>
    public static string CookiesForRequest(DownloadItem item, Uri uri, bool sameHostAndSecure = true)
    {
        if (!sameHostAndSecure) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
        if (string.IsNullOrEmpty(item.ProtectedCookies) || string.IsNullOrWhiteSpace(item.CookieHost)) return "";
        var cookieHost = item.CookieHost.Trim();
        if (!uri.Host.Equals(cookieHost, StringComparison.OrdinalIgnoreCase)) return "";
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var orig))
        {
            if (!orig.Host.Equals(cookieHost, StringComparison.OrdinalIgnoreCase))
                return uri.Scheme == Uri.UriSchemeHttps ? item.Cookies : "";
            if (orig.Scheme == Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttps)
                return "";
            bool defaultUpgrade = orig.Scheme == Uri.UriSchemeHttp && uri.Scheme == Uri.UriSchemeHttps
                                  && orig.IsDefaultPort && uri.IsDefaultPort;
            if (orig.Port != uri.Port && !defaultUpgrade)
                return "";
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "";
        }
        return item.Cookies;
    }

    /// <summary>Matches a saved <see cref="SiteLogin"/> without wildcard/prefix collisions or HTTPS-to-HTTP downgrades.</summary>
    public static SiteLogin? MatchSiteLogin(Uri uri, IEnumerable<SiteLogin>? logins = null)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var list = logins ?? Options.Logins;
        foreach (var l in list.Where(l => !string.IsNullOrWhiteSpace(l.Pattern)).OrderByDescending(l => l.Pattern.Length))
        {
            if (MatchesLoginPattern(l.Pattern, uri)) return l;
        }
        return null;
    }

    static bool MatchesLoginPattern(string rawPattern, Uri uri)
    {
        var p = rawPattern.Trim();
        if (p.Length == 0) return false;

        if (p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            p = p[8..];
        }
        else if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            p = p[7..];
        }
        else if (p.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        int slash = p.IndexOf('/');
        var hostPat = slash >= 0 ? p[..slash] : p;
        var pathPat = slash >= 0 ? p[slash..] : "";

        int colon = hostPat.IndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(hostPat[(colon + 1)..], out int port) || uri.Port != port) return false;
            hostPat = hostPat[..colon];
        }

        if (!MatchesLoginHost(hostPat, uri.Host)) return false;

        if (pathPat.Length > 0 && pathPat != "/")
        {
            var cleanPath = pathPat.TrimEnd('/');
            if (cleanPath.EndsWith("/*", StringComparison.Ordinal))
                cleanPath = cleanPath[..^2];
            if (cleanPath.Contains('*') || cleanPath.Contains('?')) return false;
            if (!uri.AbsolutePath.Equals(cleanPath, StringComparison.OrdinalIgnoreCase) &&
                !uri.AbsolutePath.StartsWith(cleanPath + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    static bool MatchesLoginHost(string hostPat, string host)
    {
        hostPat = hostPat.Trim().ToLowerInvariant();
        host = host.Trim().ToLowerInvariant();
        if (hostPat.Length == 0 || host.Length == 0) return false;
        if (hostPat.StartsWith("*."))
        {
            var suffix = hostPat[2..];
            if (suffix.Length == 0 || !suffix.Contains('.') || suffix.Contains('*') || suffix.Contains('?') || IPAddress.TryParse(suffix, out _))
                return false;
            return host.EndsWith("." + suffix, StringComparison.Ordinal);
        }
        if (hostPat.Contains('*') || hostPat.Contains('?')) return false;
        if (IPAddress.TryParse(hostPat, out _) || IPAddress.TryParse(host, out _))
            return host.Equals(hostPat, StringComparison.Ordinal);
        return host.Equals(hostPat, StringComparison.Ordinal) || host.EndsWith("." + hostPat, StringComparison.Ordinal);
    }

    public static string? ReferrerForRequest(string? referrer, Uri targetUri, bool allowReferrer = true)
    {
        if (!allowReferrer || string.IsNullOrWhiteSpace(referrer)) return null;
        if (!Uri.TryCreate(referrer.Trim(), UriKind.Absolute, out var refUri)) return null;
        if (refUri.Scheme != Uri.UriSchemeHttp && refUri.Scheme != Uri.UriSchemeHttps) return null;
        if (refUri.Scheme == Uri.UriSchemeHttps && targetUri.Scheme != Uri.UriSchemeHttps) return null;

        bool sameOrigin = refUri.Scheme.Equals(targetUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && refUri.Host.Equals(targetUri.Host, StringComparison.OrdinalIgnoreCase)
            && refUri.Port == targetUri.Port;
        if (sameOrigin)
        {
            return (refUri.UserInfo.Length > 0 || refUri.Fragment.Length > 0)
                ? new UriBuilder(refUri) { UserName = "", Password = "", Fragment = "" }.Uri.AbsoluteUri
                : refUri.AbsoluteUri;
        }
        return new UriBuilder(refUri.Scheme, refUri.Host, refUri.IsDefaultPort ? -1 : refUri.Port).Uri.AbsoluteUri;
    }

    static HttpRequestMessage NewRequest(Uri uri, RequestContext ctx, bool sameHostAndSecure, bool allowReferrer = true, bool? allowCookies = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        var ua = !string.IsNullOrWhiteSpace(ctx.UserAgent) ? ctx.UserAgent
            : !string.IsNullOrWhiteSpace(Options.ManualUserAgent) ? Options.ManualUserAgent : DefaultUserAgent;
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (ReferrerForRequest(ctx.Referrer, uri, allowReferrer) is { } safeRef)
            req.Headers.TryAddWithoutValidation("Referer", safeRef);
        var cookies = ctx.Cookies(uri, allowCookies ?? sameHostAndSecure);
        if (!string.IsNullOrWhiteSpace(cookies)) req.Headers.TryAddWithoutValidation("Cookie", cookies);

        // Credentials: the download's own login, else a saved site login. Never sent to another host or over https->http downgrade.
        string user = "", pass = "";
        if (sameHostAndSecure)
        {
            if (!string.IsNullOrWhiteSpace(ctx.Login)) { user = ctx.Login; pass = ctx.Password; }
            else if (MatchSiteLogin(uri) is { } login) { user = login.User; pass = login.Password; }
        }
        if (user.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        return req;
    }
}
