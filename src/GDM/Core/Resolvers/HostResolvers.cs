using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Golan.Gdm.Core.Resolvers;

public sealed record ResolvedLink(
    Uri Direct,
    string? FileName,
    long Size,
    IReadOnlyDictionary<string, string> Headers,
    bool NeedsMegaDecrypt = false,
    byte[]? MegaKey = null);

public interface IHostResolver
{
    bool CanHandle(Uri u);
    Task<ResolvedLink?> ResolveAsync(Uri u, HttpMessageInvoker http, CancellationToken ct);
}

public static class HostResolvers
{
    public const int DefaultMaxTextBytes = 2 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<IHostResolver> All { get; } =
    [
        new GoogleDriveResolver(),
        new DropboxResolver(),
        new OneDriveResolver(),
        new MediaFireResolver(),
        new GitHubResolver(),
        new MegaResolver(),
    ];

    /// <summary>
    /// Returns true only when <paramref name="u"/> is an absolute HTTPS URI with no embedded credentials
    /// whose host is not localhost, local/internal suffix, or a loopback/private/link-local/reserved IP.
    /// </summary>
    public static bool IsSafePublicHttpsUri(Uri? u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;

        if (!IsPublicHost(u.DnsSafeHost))
            return false;

        return true;
    }

    public static bool IsPublicHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(h)) return false;
        if (h is "localhost" or "ip6-localhost" or "ip6-loopback"
            || h.EndsWith(".localhost", StringComparison.Ordinal)
            || h.EndsWith(".local", StringComparison.Ordinal)
            || h.EndsWith(".internal", StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(h, out var ip))
            return IsPublicIpAddress(ip);

        return true;
    }

    public static bool IsPublicIpAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
            return false;

        if (ip.IsIPv4MappedToIPv6)
            return IsPublicIpAddress(ip.MapToIPv4());

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return false; // 100.64.0.0/10 CGNAT
            if (b[0] == 169 && b[1] == 254) return false;          // 169.254.0.0/16 link-local / metadata
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] >= 224) return false;                         // multicast & reserved
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            byte[] b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 Unique Local
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates an isolated <see cref="HttpMessageInvoker"/> that never shares or sends user cookies,
    /// disables automatic redirects so HTTPS-only redirect validation is enforced at every hop,
    /// and validates resolved DNS addresses against private/loopback/reserved IP ranges.
    /// </summary>
    public static HttpMessageInvoker CreateIsolatedInvoker(HttpMessageHandler? innerHandler = null)
    {
        if (innerHandler != null)
            return new HttpMessageInvoker(innerHandler, disposeHandler: false);

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var dnsHost = context.DnsEndPoint.Host;
                if (!IsPublicHost(dnsHost))
                    throw new HttpRequestException($"Blocked non-public resolver host: {dnsHost}");

                IPAddress[] addresses = await Dns.GetHostAddressesAsync(dnsHost, cancellationToken).ConfigureAwait(false);
                var publicAddresses = addresses.Where(IsPublicIpAddress).ToArray();
                if (publicAddresses.Length == 0)
                    throw new HttpRequestException($"Blocked DNS resolution to non-public IP for host: {dnsHost}");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(publicAddresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
        };
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    /// <summary>
    /// Sends an HTTP request via <paramref name="http"/> without cookies, following redirects manually
    /// up to <paramref name="maxHops"/> and strictly requiring safe public HTTPS URIs at every hop.
    /// </summary>
    public static async Task<HttpResponseMessage> SendFollowHttpsAsync(
        HttpMessageInvoker http,
        HttpRequestMessage request,
        CancellationToken ct,
        int maxHops = 10)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        var currentUri = request.RequestUri ?? throw new DownloadException(Loc.T("err.resolver.failed"));
        if (!IsSafePublicHttpsUri(currentUri))
            throw new DownloadException(Loc.T("err.resolver.failed"));

        HttpMethod currentMethod = request.Method;
        HttpContent? currentContent = request.Content;
        var visited = new HashSet<string>(StringComparer.Ordinal) { currentUri.AbsoluteUri };

        for (int hop = 0; hop <= maxHops; hop++)
        {
            using var hopReq = new HttpRequestMessage(currentMethod, currentUri);
            if (hop == 0 && currentContent != null)
                hopReq.Content = currentContent;

            foreach (var h in request.Headers)
            {
                if (string.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                hopReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            hopReq.Headers.Remove("Cookie");
            if (hopReq.Headers.UserAgent.Count == 0)
                hopReq.Headers.TryAddWithoutValidation("User-Agent", HttpDownloader.DefaultUserAgent);

            var resp = await http.SendAsync(hopReq, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc)
            {
                var nextUri = loc.IsAbsoluteUri ? loc : new Uri(currentUri, loc);
                resp.Dispose();

                if (!IsSafePublicHttpsUri(nextUri) || !visited.Add(nextUri.AbsoluteUri))
                    throw new DownloadException(Loc.T("err.resolver.failed"));

                currentUri = nextUri;
                currentMethod = HttpMethod.Get;
                currentContent = null;
                continue;
            }

            resp.RequestMessage ??= new HttpRequestMessage(currentMethod, currentUri);
            return resp;
        }

        throw new DownloadException(Loc.T("err.resolver.failed"));
    }

    /// <summary>
    /// Reads response text up to <paramref name="maxBytes"/> (default 2 MB) to prevent OOM on oversized responses.
    /// </summary>
    public static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        CancellationToken ct,
        int maxBytes = DefaultMaxTextBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is long len && len > maxBytes)
            throw new DownloadException(Loc.T("err.resolver.failed"));

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (total > maxBytes)
                throw new DownloadException(Loc.T("err.resolver.failed"));
            ms.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Attempts to resolve a sharing link through the registered resolvers in order.
    /// Applies a 20-second timeout by default and returns <c>null</c> on timeout or error so the caller
    /// can fall back to the raw URL.
    /// </summary>
    public static async Task<ResolvedLink?> TryResolveAsync(
        Uri u,
        HttpMessageInvoker? http = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        if (!IsSafePublicHttpsUri(u)) return null;

        IHostResolver? matched = null;
        foreach (var r in All)
        {
            if (r.CanHandle(u))
            {
                matched = r;
                break;
            }
        }
        if (matched is null) return null;

        bool ownsInvoker = http is null;
        var invoker = http ?? CreateIsolatedInvoker();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));

        try
        {
            var resolved = await matched.ResolveAsync(u, invoker, timeoutCts.Token).ConfigureAwait(false);
            if (resolved is null) return null;
            if (!IsSafePublicHttpsUri(resolved.Direct))
                return null;
            return resolved;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"resolver fallback for {u.Host}: {ex.Message}");
            return null;
        }
        finally
        {
            if (ownsInvoker) invoker.Dispose();
        }
    }

    public static string? SanitizeDecodedFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Replace('/', '\\').Trim();
        var leaf = Path.GetFileName(normalized).Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(leaf)) return null;
        var safe = FileNames.Sanitize(leaf);
        return string.IsNullOrWhiteSpace(safe) ? null : safe;
    }

    public static (string? FileName, long Size) ReadResponseMeta(HttpResponseMessage resp, Uri? fallbackUri = null)
    {
        long size = resp.StatusCode == HttpStatusCode.PartialContent
            ? (resp.Content.Headers.ContentRange?.Length ?? -1)
            : (resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? -1);

        string? name = null;
        var cd = resp.Content.Headers.ContentDisposition;
        if (cd != null)
        {
            var candidate = (cd.FileNameStar ?? cd.FileName)?.Trim('"', '\'', ' ');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                try { candidate = Uri.UnescapeDataString(candidate); }
                catch (UriFormatException) { /* Keep raw candidate if percent-encoding is malformed */ }
                name = SanitizeDecodedFileName(candidate);
            }
        }

        if (string.IsNullOrWhiteSpace(name) && fallbackUri != null)
        {
            var fromUrl = FileNames.FromUrl(fallbackUri.ToString());
            if (!string.IsNullOrWhiteSpace(fromUrl) && !string.Equals(fromUrl, "download", StringComparison.OrdinalIgnoreCase))
                name = SanitizeDecodedFileName(fromUrl);
        }

        return (name, size);
    }
}
