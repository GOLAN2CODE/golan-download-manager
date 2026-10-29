using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Golan.Gdm.Integration.Remote;

public sealed record RemoteItemDto(
    Guid Id,
    string Name,
    string Status,
    double Progress,
    double Speed,
    long Size,
    long Downloaded,
    Guid? Queue,
    string Category);

public sealed record RemoteQueueDto(Guid Id, string Name, bool Running);

public sealed record RemoteInfoDto(
    string Version,
    double TotalSpeed,
    int ActiveCount,
    bool SpeedLimitEnabled,
    int SpeedLimitKB,
    IReadOnlyList<RemoteQueueDto> Queues);

public sealed record RemoteAddRequest(string? Url, Guid? Queue = null, bool? Start = null);
public sealed record RemoteLimiterRequest(bool On);
public sealed record RemotePairRequest(string? Pin, string? Device);
public sealed record RemotePairResponse(string Token, string DeviceId, string Device);

public interface IRemoteTarget
{
    IReadOnlyList<RemoteItemDto> GetItems();
    RemoteInfoDto GetInfo();
    Guid Add(string url, Guid? queueId, bool start);
    bool Pause(Guid id);
    bool Resume(Guid id);
    bool RemoveFromListOnly(Guid id);
    bool StartQueue(Guid id);
    bool StopQueue(Guid id);
    void SetSpeedLimiter(bool enabled);
}

/// <summary>Thread-safe adapter bridging <see cref="DownloadManager"/> to <see cref="IRemoteTarget"/> on the WPF UI thread.</summary>
public sealed class DownloadManagerRemoteTarget(DownloadManager manager) : IRemoteTarget
{
    static T OnUi<T>(Func<T> fn)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null || disp.CheckAccess() || disp.HasShutdownStarted || disp.HasShutdownFinished || !disp.Thread.IsAlive)
            return fn();
        return disp.Invoke(fn);
    }

    public IReadOnlyList<RemoteItemDto> GetItems() => OnUi(() =>
        manager.Items.Select(i => new RemoteItemDto(
            i.Id,
            i.FileName,
            i.Status.ToString(),
            Math.Round(i.Progress, 1),
            i.Speed,
            i.TotalSize,
            i.Downloaded,
            i.QueueId,
            i.Category)).ToList());

    public RemoteInfoDto GetInfo() => OnUi(() =>
    {
        var ver = typeof(DownloadManager).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var queues = manager.Settings.Queues
            .Select(q => new RemoteQueueDto(q.Id, q.Name, manager.IsQueueRunning(q.Id)))
            .ToList();
        return new RemoteInfoDto(
            ver,
            manager.TotalSpeed,
            manager.ActiveCount,
            manager.Settings.SpeedLimitEnabled,
            manager.Settings.SpeedLimitKB,
            queues);
    });

    public Guid Add(string url, Guid? queueId, bool start) => OnUi(() =>
    {
        if (manager.Settings.Queues.Count == 0)
            manager.Settings.Normalize();
        Guid? validQueueId = queueId is { } qid && manager.Settings.FindQueue(qid) != null ? qid : null;
        if (url != null && url.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (!MagnetValidator.IsMagnetUri(url))
                throw new ArgumentException("Invalid magnet URI.", nameof(url));
            var magItem = manager.AddMagnet(url, startNow: false);
            if (validQueueId != null)
                magItem.QueueId = validQueueId;
            manager.Save();
            if (start)
                manager.Start(magItem);
            return magItem.Id;
        }

        var item = manager.CreateHttp(url ?? "");
        manager.Add(item, startNow: start, queueId: validQueueId);
        return item.Id;
    });

    public bool Pause(Guid id) => OnUi(() =>
    {
        var item = manager.Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return false;
        manager.Pause(item);
        return true;
    });

    public bool Resume(Guid id) => OnUi(() =>
    {
        var item = manager.Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return false;
        manager.Start(item);
        return true;
    });

    public bool RemoveFromListOnly(Guid id) => OnUi(() =>
    {
        var item = manager.Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return false;
        // Remote delete NEVER deletes downloaded files on disk; list removal only.
        _ = manager.RemoveAsync(item, deleteFile: false);
        return true;
    });

    public bool StartQueue(Guid id) => OnUi(() =>
    {
        var q = manager.Settings.FindQueue(id);
        if (q == null) return false;
        manager.StartQueue(id);
        return true;
    });

    public bool StopQueue(Guid id) => OnUi(() =>
    {
        var q = manager.Settings.FindQueue(id);
        if (q == null) return false;
        manager.StopQueue(id);
        return true;
    });

    public void SetSpeedLimiter(bool enabled) => OnUi(() =>
    {
        var app = Application.Current;
        var setSpeedLimit = app?.GetType().GetMethod("SetSpeedLimit", [typeof(bool)]);
        if (setSpeedLimit != null)
        {
            setSpeedLimit.Invoke(app, [enabled]);
        }
        else
        {
            manager.Settings.SpeedLimitEnabled = enabled;
            manager.ApplyLimiter();
            manager.Settings.Save();
        }
        return true;
    });
}

public sealed class RemoteAuthManager
{
    public const int MaxFailedAttemptsPerWindow = 5;
    public const int MaxGlobalFailuresPerPin = 25;
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    sealed class IpAttemptTracker
    {
        public readonly List<DateTimeOffset> Failures = [];
        public DateTimeOffset? LockedUntil;
    }

    readonly Lock _gate = new();
    readonly Action? _onSave;
    readonly Func<DateTimeOffset> _clock;
    readonly Dictionary<string, IpAttemptTracker> _attempts = new(StringComparer.OrdinalIgnoreCase);
    string? _activePin;
    DateTimeOffset? _pinExpiresAt;
    int _globalFailuresForActivePin;

    public RemoteAuthManager(List<PairedDevice> devices, Action? onSave = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        Devices = devices;
        _onSave = onSave;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public List<PairedDevice> Devices { get; }

    public IReadOnlyList<PairedDevice> GetDevicesSnapshot()
    {
        lock (_gate)
        {
            return Devices.Select(d => new PairedDevice
            {
                Id = d.Id,
                Name = d.Name,
                TokenHash = d.TokenHash,
                CreatedAt = d.CreatedAt,
                LastSeenAt = d.LastSeenAt,
            }).ToList();
        }
    }

    public string? ActivePin
    {
        get
        {
            lock (_gate)
            {
                if (_activePin != null && _pinExpiresAt != null && _clock() <= _pinExpiresAt.Value)
                    return _activePin;
                return null;
            }
        }
    }

    public DateTimeOffset? PinExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _activePin != null && _pinExpiresAt != null && _clock() <= _pinExpiresAt.Value
                    ? _pinExpiresAt
                    : null;
            }
        }
    }

    public string GeneratePin()
    {
        lock (_gate)
        {
            int val = RandomNumberGenerator.GetInt32(0, 1_000_000);
            _activePin = val.ToString("D6");
            _pinExpiresAt = _clock().Add(PinLifetime);
            _globalFailuresForActivePin = 0;
            return _activePin;
        }
    }

    public (bool Ok, bool LockedOut, string? Token, PairedDevice? Device) TryPair(string ip, string? pin, string? deviceName)
    {
        var clientIp = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
        lock (_gate)
        {
            var now = _clock();
            if (_attempts.Count > 256)
            {
                var staleKeys = _attempts
                    .Where(kv => (kv.Value.LockedUntil == null || now >= kv.Value.LockedUntil.Value)
                                 && kv.Value.Failures.All(t => now - t > RateLimitWindow))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in staleKeys) _attempts.Remove(k);
            }

            if (!_attempts.TryGetValue(clientIp, out var tracker))
                _attempts[clientIp] = tracker = new IpAttemptTracker();

            if (tracker.LockedUntil is { } lockedUntil)
            {
                if (now < lockedUntil)
                    return (false, true, null, null);
                tracker.LockedUntil = null;
                tracker.Failures.Clear();
            }

            tracker.Failures.RemoveAll(t => now - t > RateLimitWindow);
            if (tracker.Failures.Count >= MaxFailedAttemptsPerWindow)
            {
                tracker.LockedUntil = now.Add(LockoutDuration);
                return (false, true, null, null);
            }

            var candidatePin = (pin ?? "").Trim();
            bool hasValidPin = _activePin != null
                && _pinExpiresAt != null
                && now <= _pinExpiresAt.Value
                && _globalFailuresForActivePin < MaxGlobalFailuresPerPin
                && candidatePin.Length == 6
                && ConstantTimeEquals(_activePin, candidatePin);

            if (!hasValidPin)
            {
                tracker.Failures.Add(now);
                _globalFailuresForActivePin++;
                if (_globalFailuresForActivePin >= MaxGlobalFailuresPerPin)
                {
                    _activePin = null;
                    _pinExpiresAt = null;
                }
                if (tracker.Failures.Count >= MaxFailedAttemptsPerWindow)
                {
                    tracker.LockedUntil = now.Add(LockoutDuration);
                    return (false, true, null, null);
                }
                return (false, false, null, null);
            }

            // Valid PIN: consume immediately (single-use) and clear IP failures
            _activePin = null;
            _pinExpiresAt = null;
            _globalFailuresForActivePin = 0;
            tracker.Failures.Clear();
            tracker.LockedUntil = null;

            byte[] rawToken = RandomNumberGenerator.GetBytes(32); // 256-bit CSPRNG token
            string token = Convert.ToBase64String(rawToken)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            string tokenHash = HashToken(token);

            string cleanName = string.IsNullOrWhiteSpace(deviceName) ? "Phone" : deviceName.Trim();
            if (cleanName.Length > 64) cleanName = cleanName[..64];

            var device = new PairedDevice
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = cleanName,
                TokenHash = tokenHash,
                CreatedAt = now,
                LastSeenAt = now,
            };

            Devices.Add(device);
            _onSave?.Invoke();
            return (true, false, token, device);
        }
    }

    public PairedDevice? AuthenticateBearer(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;

        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = authorizationHeader[prefix.Length..].Trim();
        if (token.Length < 16 || token.Length > 256)
            return null;

        var candidateHash = HashToken(token);
        lock (_gate)
        {
            PairedDevice? matched = null;
            foreach (var d in Devices)
            {
                if (ConstantTimeEquals(d.TokenHash, candidateHash))
                    matched = d;
            }
            if (matched != null)
                matched.LastSeenAt = _clock();
            return matched;
        }
    }

    public bool RevokeDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_gate)
        {
            int removed = Devices.RemoveAll(d => string.Equals(d.Id, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _onSave?.Invoke();
                return true;
            }
            return false;
        }
    }

    public int RevokeAllDevices()
    {
        lock (_gate)
        {
            int count = Devices.Count;
            if (count > 0)
            {
                Devices.Clear();
                _onSave?.Invoke();
            }
            return count;
        }
    }

    public static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null) return false;
        byte[] aBytes = Encoding.UTF8.GetBytes(a);
        byte[] bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
        {
            // Perform dummy constant-time comparison to avoid early branch timing skew
            CryptographicOperations.FixedTimeEquals(aBytes, aBytes);
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    static string HashToken(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}

public sealed class RemoteServer : IAsyncDisposable
{
    public const int MaxRequestBodyBytes = 16 * 1024;

    static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    readonly IRemoteTarget _target;
    readonly RemoteAuthManager _auth;
    readonly IPAddress _bindAddress;
    readonly int _requestedPort;
    WebApplication? _app;
    X509Certificate2? _tlsCert;

    public RemoteServer(IRemoteTarget target, RemoteAuthManager auth, IPAddress bindAddress, int port = 8765)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(auth);
        if (!IsAllowedBindAddress(bindAddress))
            throw new ArgumentException("RemoteServer bind address must be a specific local/LAN IPv4 address (never 0.0.0.0, IPv6, broadcast, or public).", nameof(bindAddress));
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535.");

        _target = target;
        _auth = auth;
        _bindAddress = bindAddress;
        _requestedPort = port;
        Port = port;
    }

    public int Port { get; private set; }
    public IPAddress BindAddress => _bindAddress;
    public Uri BaseUri => new($"https://{_bindAddress}:{Port}/");
    public bool IsRunning => _app != null;
    public string CertificateFingerprint { get; private set; } = "";
    public string CertificateFingerprintGroups { get; private set; } = "";
    public string CertificateFingerprintColon { get; private set; } = "";
    public string QrPayloadUri => RemoteTls.BuildPairingUrl(BaseUri, CertificateFingerprint);

    /// <summary>
    /// Validates that <paramref name="address"/> is a specific local/LAN IPv4 interface address.
    /// Rejects 0.0.0.0 (IPAddress.Any), IPv6, broadcast, multicast, and public WAN addresses.
    /// </summary>
    public static bool IsAllowedBindAddress(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            return false;

        byte[] b = address.GetAddressBytes();
        if (b.Length != 4) return false;

        // 127.0.0.0/8 (Loopback)
        if (b[0] == 127) return true;
        // 10.0.0.0/8 (RFC 1918 Private LAN)
        if (b[0] == 10) return true;
        // 172.16.0.0/12 (RFC 1918 Private LAN)
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
        // 192.168.0.0/16 (RFC 1918 Private LAN)
        if (b[0] == 192 && b[1] == 168) return true;
        // 169.254.0.0/16 (Link-Local)
        if (b[0] == 169 && b[1] == 254) return true;
        // 100.64.0.0/10 (Carrier-Grade NAT / Tailscale LAN)
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;

        return false;
    }

    /// <summary>Enumerates active local/LAN IPv4 interface addresses (always including 127.0.0.1).</summary>
    public static IReadOnlyList<IPAddress> GetLocalIpv4Interfaces()
    {
        var result = new List<IPAddress> { IPAddress.Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (IsAllowedBindAddress(ua.Address) && !result.Contains(ua.Address))
                        result.Add(ua.Address);
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>Validates that a remote-submitted URL uses strictly http, https, ftp, or magnet and is not a local/UNC/script path.</summary>
    public static bool IsAllowedRemoteUrl(string? url, out Uri? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (trimmed.Length > 4096 || trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (trimmed.Any(c => char.IsControl(c)))
            return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "magnet")
        {
            if (!MagnetValidator.TryParse(trimmed, out _, out _))
                return false;
            normalized = uri;
            return true;
        }

        if (scheme is "http" or "https" or "ftp")
        {
            if (string.IsNullOrWhiteSpace(uri.Host) || uri.IsUnc || uri.IsFile)
                return false;
            normalized = uri;
            return true;
        }

        return false;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app != null) return;

        var additionalLanIps = _bindAddress.Equals(IPAddress.Loopback) ? null : GetLocalIpv4Interfaces();
        var tlsCert = RemoteTls.EnsureCertificate(_bindAddress, additionalLanIps);
        _tlsCert = tlsCert;
        CertificateFingerprint = RemoteTls.GetSha256FingerprintHex(tlsCert);
        CertificateFingerprintGroups = RemoteTls.FormatFingerprintShortGroups(tlsCert);
        CertificateFingerprintColon = RemoteTls.FormatFingerprintColonBytes(tlsCert);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 256 * 1024;
            options.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = tlsCert;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            });
            options.Listen(_bindAddress, _requestedPort, listenOptions =>
            {
                listenOptions.UseHttps(tlsCert);
            });
        });

        var app = builder.Build();
        var htmlTemplate = LoadEmbeddedHtml();

        // Security headers, request size enforcement, Content-Type verification, and Bearer authentication.
        // Note: Strict-Transport-Security (HSTS) is intentionally omitted because RFC 6797 forbids HSTS on
        // IP literals and bars user click-through on self-signed certificates, and DHCP LAN IP reassignment
        // would break other non-TLS local devices later assigned the same IP.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";

            if (ctx.Request.ContentLength is > MaxRequestBodyBytes)
            {
                await DrainUpToAsync(ctx.Request.Body, 256 * 1024, ctx.RequestAborted);
                ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" }, WebJsonOptions);
                return;
            }

            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                var secFetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
                if (!string.IsNullOrEmpty(secFetchSite)
                    && !string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase))
                {
                    await DrainUpToAsync(ctx.Request.Body, MaxRequestBodyBytes, ctx.RequestAborted);
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { error = "cross_site_forbidden" }, WebJsonOptions);
                    return;
                }
                if (!path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase))
                {
                    // Strictly inspect Authorization header only; cookies are completely ignored to prevent CSRF.
                    var authHeader = ctx.Request.Headers.Authorization.ToString();
                    if (_auth.AuthenticateBearer(authHeader) == null)
                    {
                        await DrainUpToAsync(ctx.Request.Body, MaxRequestBodyBytes, ctx.RequestAborted);
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }, WebJsonOptions);
                        return;
                    }
                }

                if (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method))
                {
                    var ctHeader = ctx.Request.ContentType ?? "";
                    if (!ctHeader.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        await DrainUpToAsync(ctx.Request.Body, MaxRequestBodyBytes, ctx.RequestAborted);
                        ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                        await ctx.Response.WriteAsJsonAsync(new { error = "json_required" }, WebJsonOptions);
                        return;
                    }
                }
            }

            try
            {
                await next();
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" }, WebJsonOptions);
                }
            }
        });

        app.MapGet("/", async (HttpContext ctx) =>
        {
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

            var acceptLang = ctx.Request.Headers.AcceptLanguage.ToString();
            bool preferArabic = PreferArabic(acceptLang);
            var rendered = htmlTemplate
                .Replace("__LANG__", preferArabic ? "ar" : "en", StringComparison.Ordinal)
                .Replace("__DIR__", preferArabic ? "rtl" : "ltr", StringComparison.Ordinal)
                .Replace("__CERT_FP_GROUPS__", CertificateFingerprintGroups, StringComparison.Ordinal)
                .Replace("__CERT_FP_COLON__", CertificateFingerprintColon, StringComparison.Ordinal)
                .Replace("__CERT_FP_HEX__", CertificateFingerprint, StringComparison.Ordinal);

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(rendered, Encoding.UTF8);
        });

        app.MapPost("/api/pair", async (HttpContext ctx) =>
        {
            var body = await ReadJsonBodyAsync<RemotePairRequest>(ctx);
            if (body == null) return;

            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var (ok, lockedOut, token, device) = _auth.TryPair(ip, body.Pin, body.Device);
            if (lockedOut)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsJsonAsync(new { error = "locked_out" }, WebJsonOptions);
                return;
            }
            if (!ok || token == null || device == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_pin" }, WebJsonOptions);
                return;
            }

            await ctx.Response.WriteAsJsonAsync(new RemotePairResponse(token, device.Id, device.Name), WebJsonOptions);
        });

        app.MapGet("/api/items", async (HttpContext ctx) =>
        {
            var items = _target.GetItems();
            await ctx.Response.WriteAsJsonAsync(items, WebJsonOptions);
        });

        app.MapGet("/api/info", async (HttpContext ctx) =>
        {
            var info = _target.GetInfo();
            await ctx.Response.WriteAsJsonAsync(info, WebJsonOptions);
        });

        app.MapPost("/api/add", async (HttpContext ctx) =>
        {
            var body = await ReadJsonBodyAsync<RemoteAddRequest>(ctx);
            if (body == null) return;

            if (!IsAllowedRemoteUrl(body.Url, out var normalizedUri) || normalizedUri == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_url" }, WebJsonOptions);
                return;
            }

            // Always ignore any client-supplied folder; use server-configured category folder.
            var id = _target.Add(normalizedUri.AbsoluteUri, body.Queue, body.Start ?? true);
            await ctx.Response.WriteAsJsonAsync(new { id }, WebJsonOptions);
        });

        app.MapPost("/api/items/{id:guid}/pause", async (Guid id, HttpContext ctx) =>
        {
            if (!await DrainOptionalBodyAsync(ctx)) return;
            if (!_target.Pause(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "not_found" }, WebJsonOptions);
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { ok = true }, WebJsonOptions);
        });

        app.MapPost("/api/items/{id:guid}/resume", async (Guid id, HttpContext ctx) =>
        {
            if (!await DrainOptionalBodyAsync(ctx)) return;
            if (!_target.Resume(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "not_found" }, WebJsonOptions);
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { ok = true }, WebJsonOptions);
        });

        app.MapDelete("/api/items/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            if (!_target.RemoveFromListOnly(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "not_found" }, WebJsonOptions);
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { ok = true }, WebJsonOptions);
        });

        app.MapPost("/api/queues/{id:guid}/start", async (Guid id, HttpContext ctx) =>
        {
            if (!await DrainOptionalBodyAsync(ctx)) return;
            if (!_target.StartQueue(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "not_found" }, WebJsonOptions);
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { ok = true }, WebJsonOptions);
        });

        app.MapPost("/api/queues/{id:guid}/stop", async (Guid id, HttpContext ctx) =>
        {
            if (!await DrainOptionalBodyAsync(ctx)) return;
            if (!_target.StopQueue(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "not_found" }, WebJsonOptions);
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { ok = true }, WebJsonOptions);
        });

        app.MapPost("/api/limiter", async (HttpContext ctx) =>
        {
            var body = await ReadJsonBodyAsync<RemoteLimiterRequest>(ctx);
            if (body == null) return;
            _target.SetSpeedLimiter(body.On);
            await ctx.Response.WriteAsJsonAsync(new { ok = true, on = body.On }, WebJsonOptions);
        });

        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            try { await app.DisposeAsync(); } catch { }
            var cert = Interlocked.Exchange(ref _tlsCert, null);
            cert?.Dispose();
            throw;
        }

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is { Count: > 0 } && Uri.TryCreate(addresses.First(), UriKind.Absolute, out var boundUri))
            Port = boundUri.Port;

        _app = app;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app != null)
        {
            try { await app.StopAsync(ct); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }
        var cert = Interlocked.Exchange(ref _tlsCert, null);
        cert?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    static async Task<T?> ReadJsonBodyAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int total = 0;
            while (true)
            {
                int read = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted);
                if (read == 0) break;
                total += read;
                if (total > MaxRequestBodyBytes)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" }, WebJsonOptions);
                    return null;
                }
                ms.Write(buf, 0, read);
            }

            if (ms.Length == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "empty_body" }, WebJsonOptions);
                return null;
            }

            ms.Position = 0;
            var obj = JsonSerializer.Deserialize<T>(ms, WebJsonOptions);
            if (obj == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" }, WebJsonOptions);
                return null;
            }
            return obj;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" }, WebJsonOptions);
            return null;
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" }, WebJsonOptions);
            return null;
        }
    }

    static async Task<bool> DrainOptionalBodyAsync(HttpContext ctx)
    {
        try
        {
            var buf = new byte[1024];
            int total = 0;
            while (true)
            {
                int read = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted);
                if (read == 0) return true;
                total += read;
                if (total > MaxRequestBodyBytes)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" }, WebJsonOptions);
                    return false;
                }
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" }, WebJsonOptions);
            return false;
        }
    }

    static async Task DrainUpToAsync(Stream body, int maxBytes, CancellationToken ct)
    {
        try
        {
            var buf = new byte[4096];
            int total = 0;
            while (total < maxBytes)
            {
                int n = await body.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, maxBytes - total)), ct);
                if (n <= 0) break;
                total += n;
            }
        }
        catch { }
    }

    static bool PreferArabic(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage)) return false;
        var first = acceptLanguage.Split(',')[0].Trim();
        return first.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
    }

    static string LoadEmbeddedHtml()
    {
        var asm = typeof(RemoteServer).Assembly;
        var stream = asm.GetManifestResourceStream("Remote/remote.html");
        if (stream == null)
        {
            var fallback = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("remote.html", StringComparison.OrdinalIgnoreCase));
            if (fallback != null)
                stream = asm.GetManifestResourceStream(fallback);
        }
        if (stream != null)
        {
            using (stream)
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }
        throw new InvalidOperationException("Embedded resource 'Remote/remote.html' not found.");
    }
}
