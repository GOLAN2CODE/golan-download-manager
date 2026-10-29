using System.Diagnostics;
using System.Text;

namespace Golan.Gdm.Core;

/// <summary>Challenge = the site's protection (e.g. Cloudflare) wants a browser check: retrying only makes it stricter.</summary>
public enum FailKind { General, Retryable, Expired, Mismatch, Rejected, Challenge }

public sealed class DownloadException(string message, FailKind kind = FailKind.General) : Exception(message)
{
    public FailKind Kind { get; } = kind;
    public bool Retryable => Kind is FailKind.Retryable or FailKind.Rejected;
    public TimeSpan? RetryAfter { get; init; }

    public DownloadException(string message, bool retryable) : this(message, retryable ? FailKind.Retryable : FailKind.General) { }
}

/// <summary>Token-bucket speed limiter (global, or one per file).</summary>
public sealed class SpeedLimiter
{
    readonly object _gate = new();
    double _tokens;
    long _last = Stopwatch.GetTimestamp();

    public bool Enabled { get; set; }
    public long BytesPerSecond { get; set; } = 500 * 1024;

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        while (Enabled && BytesPerSecond > 0)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _last) / (double)Stopwatch.Frequency;
                _last = now;
                _tokens = Math.Min(BytesPerSecond, _tokens + elapsed * BytesPerSecond);
                if (_tokens > 0) { _tokens -= bytes; return; }
                wait = TimeSpan.FromSeconds(Math.Min(1.0, -_tokens / BytesPerSecond + 0.005));
            }
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Caps simultaneous connections per host across all downloads (IDM "max connections for a server").</summary>
public static class HostGate
{
    sealed class Gate { public int Limit; public int InUse; public readonly SemaphoreSlim Signal = new(0); }
    static readonly Dictionary<string, Gate> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> AcquireAsync(string host, int? limit, CancellationToken ct)
    {
        if (limit is null or <= 0) return Nothing.Instance;
        while (true)
        {
            Gate g;
            lock (Gates)
            {
                if (!Gates.TryGetValue(host, out g!)) Gates[host] = g = new Gate();
                g.Limit = limit.Value;
                if (g.InUse < g.Limit) { g.InUse++; return new Release(g); }
            }
            await g.Signal.WaitAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    sealed class Release(Gate g) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (Gates) g.InUse--;
            g.Signal.Release();
        }
    }

    sealed class Nothing : IDisposable { public static readonly Nothing Instance = new(); public void Dispose() { } }
}

/// <summary>Paths claimed by unfinished downloads, so two items never share a file or a temp file.</summary>
public static class PathRegistry
{
    static readonly Dictionary<string, Guid> Owners = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsClaimedByOther(Guid id, string fullPath)
    {
        lock (Owners) return Owners.TryGetValue(fullPath, out var owner) && owner != id;
    }

    public static void Claim(Guid id, string fullPath)
    {
        lock (Owners)
        {
            if (!Owners.TryGetValue(fullPath, out var owner) || owner == id)
                Owners[fullPath] = id;
        }
    }

    public static void Release(Guid id)
    {
        lock (Owners)
            foreach (var k in Owners.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) Owners.Remove(k);
    }

    /// <summary>Returns a free path for this item ("name (1).ext" ...) and claims it.</summary>
    public static string Reserve(Guid id, string fullPath)
    {
        lock (Owners)
        {
            var dir = Path.GetDirectoryName(fullPath)!;
            var stem = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);
            var candidate = fullPath;
            for (int i = 1; ; i++)
            {
                bool takenByOther = Owners.TryGetValue(candidate, out var owner) && owner != id;
                bool tempTaken = File.Exists(candidate + ".gdm") && owner != id;
                if (!takenByOther && !File.Exists(candidate) && !tempTaken) break;
                candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            }
            foreach (var k in Owners.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) Owners.Remove(k);
            Owners[candidate] = id;
            return candidate;
        }
    }
}

public sealed record ProbeResult(Uri FinalUri, long Size, bool Resumable, string? FileName, string? Mime, string ETag, DateTimeOffset? LastModified);

/// <summary>Writes the Zone.Identifier stream so Windows SmartScreen and Office treat the file as downloaded from the internet.</summary>
public static class MarkOfTheWeb
{
    public static void Write(string path, string? hostUrl, string? referrer = null)
    {
        WriteRawPayload(path, BuildPayload(hostUrl, referrer));
    }

    public static string BuildPayload(string? hostUrl, string? referrer = null)
    {
        var sb = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (Uri.TryCreate(referrer, UriKind.Absolute, out var r) &&
            (r.Scheme == Uri.UriSchemeHttp || r.Scheme == Uri.UriSchemeHttps))
        {
            sb.Append("ReferrerUrl=").Append(r.GetLeftPart(UriPartial.Path)).Append("\r\n");
        }
        if (Uri.TryCreate(hostUrl, UriKind.Absolute, out var h))
        {
            if (h.Scheme == Uri.UriSchemeHttp || h.Scheme == Uri.UriSchemeHttps || h.Scheme == Uri.UriSchemeFtp)
                sb.Append("HostUrl=").Append(h.GetLeftPart(UriPartial.Path)).Append("\r\n");
            else if (string.Equals(h.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
                sb.Append("HostUrl=magnet:\r\n");
        }
        return sb.ToString();
    }

    public static string ReadZonePayloadOrDefault(string? sourcePath, string? fallbackHostUrl = null, string? fallbackReferrer = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                string adsPath = sourcePath + ":Zone.Identifier";
                if (File.Exists(adsPath))
                {
                    string existing = File.ReadAllText(adsPath);
                    if (!string.IsNullOrWhiteSpace(existing) && existing.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase))
                        return existing;
                }
            }
        }
        catch { /* ignore ADS read error */ }
        return BuildPayload(fallbackHostUrl, fallbackReferrer);
    }

    public static void WriteRawPayload(string path, string? zonePayload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            string payload = !string.IsNullOrWhiteSpace(zonePayload) && zonePayload.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase)
                ? zonePayload
                : "[ZoneTransfer]\r\nZoneId=3\r\n";
            File.WriteAllText(path + ":Zone.Identifier", payload);
        }
        catch (Exception ex) { Log.Write("motw failed: " + ex.Message); }
    }
}
