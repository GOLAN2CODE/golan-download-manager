using System.Collections.Concurrent;

namespace Golan.Gdm.Core;

/// <summary>
/// Servers behind rate limiters (Cloudflare and others) answer "busy" (429/503) when a download opens too many
/// connections. Like IDM, Golan then keeps the connections that work, opens no new ones, remembers the limit for
/// that server and waits patiently on the last connection instead of failing.
/// </summary>
public sealed partial class HttpDownloader
{
    /// <summary>Extra waits allowed when the last connection hears "busy" (it is rate limiting, not broken).</summary>
    public static int MaxBusyRetries { get; set; } = 12;

    /// <summary>How long a learned "this server accepts only N connections" is kept.</summary>
    public static TimeSpan LearnedLimitLifetime { get; set; } = TimeSpan.FromMinutes(30);

    static readonly ConcurrentDictionary<string, (int Limit, DateTime UntilUtc)> LearnedLimits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A connection was refused in this run: no new connections or splits until it ends.</summary>
    volatile bool _serverBusy;

    public static void LearnHostLimit(string host, int accepted)
    {
        if (string.IsNullOrEmpty(host)) return;
        accepted = Math.Max(1, accepted);
        var until = DateTime.UtcNow + LearnedLimitLifetime;
        var now = LearnedLimits.AddOrUpdate(host, (accepted, until),
            (_, old) => (Math.Min(old.UntilUtc > DateTime.UtcNow ? old.Limit : int.MaxValue, accepted), until));
        Log.Write($"server busy: {host} now gets at most {now.Limit} connection(s)");
    }

    public static int? LearnedHostLimit(string host)
        => !string.IsNullOrEmpty(host) && LearnedLimits.TryGetValue(host, out var v) && v.UntilUtc > DateTime.UtcNow ? v.Limit : null;

    public static void ForgetLearnedLimits() => LearnedLimits.Clear();

    /// <summary>Wait before asking a busy server again: its Retry-After when given, otherwise growing up to 20 s.</summary>
    static TimeSpan BusyWait(int busyRetries, TimeSpan? retryAfter) => Backoff(Math.Min(busyRetries + 1, 5), retryAfter);
}
