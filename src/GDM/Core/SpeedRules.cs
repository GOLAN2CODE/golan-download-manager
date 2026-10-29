namespace Golan.Gdm.Core;

[Flags]
public enum DaysMask
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Sunday | Monday | Tuesday | Wednesday | Thursday | Friday | Saturday,
}

public sealed class SpeedRule
{
    public DaysMask Days { get; set; } = DaysMask.All;
    public TimeOnly From { get; set; } = new(0, 0);
    public TimeOnly To { get; set; } = new(6, 0);
    /// <summary>Speed limit in KB/s; 0 means unlimited.</summary>
    public int LimitKBps { get; set; } = 500;
    public bool Enabled { get; set; } = true;
}

public static class SpeedRules
{
    public static DaysMask MaskFor(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => DaysMask.Sunday,
        DayOfWeek.Monday => DaysMask.Monday,
        DayOfWeek.Tuesday => DaysMask.Tuesday,
        DayOfWeek.Wednesday => DaysMask.Wednesday,
        DayOfWeek.Thursday => DaysMask.Thursday,
        DayOfWeek.Friday => DaysMask.Friday,
        DayOfWeek.Saturday => DaysMask.Saturday,
        _ => DaysMask.None,
    };

    /// <summary>
    /// Evaluates the rules list at <paramref name="localNow"/>. First enabled matching rule wins.
    /// Overnight ranges (From &gt; To) belong to the start day; To is exclusive.
    /// Returns LimitKBps (0 = unlimited) or null if no rule matches.
    /// </summary>
    public static int? Evaluate(IEnumerable<SpeedRule>? rules, DateTime localNow)
    {
        if (rules == null) return null;
        var time = TimeOnly.FromDateTime(localNow);
        var todayMask = MaskFor(localNow.DayOfWeek);
        var yesterdayMask = MaskFor(localNow.Date.AddDays(-1).DayOfWeek);

        foreach (var rule in rules)
        {
            if (rule == null || !rule.Enabled || rule.Days == DaysMask.None)
                continue;

            bool matches;
            if (rule.From == rule.To)
            {
                // Full 24-hour rule on matching days
                matches = (rule.Days & todayMask) != 0;
            }
            else if (rule.From < rule.To)
            {
                // Same-day [From, To)
                matches = (rule.Days & todayMask) != 0 && time >= rule.From && time < rule.To;
            }
            else
            {
                // Overnight range (From > To): belongs strictly to the start day
                if (time >= rule.From)
                    matches = (rule.Days & todayMask) != 0;
                else if (time < rule.To)
                    matches = (rule.Days & yesterdayMask) != 0;
                else
                    matches = false;
            }

            if (matches)
                return Math.Max(0, rule.LimitKBps);
        }

        return null;
    }

    /// <summary>Returns the next local DateTime strictly after <paramref name="localNow"/> when a rule starts or ends.</summary>
    public static DateTime? NextBoundary(IEnumerable<SpeedRule>? rules, DateTime localNow)
    {
        if (rules == null) return null;
        var enabledRules = rules.Where(r => r is { Enabled: true } && r.Days != DaysMask.None).ToList();
        if (enabledRules.Count == 0) return null;

        DateTime? best = null;
        void Consider(DateTime candidate)
        {
            if (candidate > localNow && (best == null || candidate < best.Value))
                best = candidate;
        }

        for (int offset = -1; offset <= 8; offset++)
        {
            var anchorDate = localNow.Date.AddDays(offset);
            var mask = MaskFor(anchorDate.DayOfWeek);

            foreach (var r in enabledRules)
            {
                if ((r.Days & mask) == 0) continue;

                var start = anchorDate.Add(r.From.ToTimeSpan());
                DateTime end;
                if (r.From < r.To)
                    end = anchorDate.Add(r.To.ToTimeSpan());
                else if (r.From > r.To)
                    end = anchorDate.AddDays(1).Add(r.To.ToTimeSpan());
                else
                    end = anchorDate.AddDays(1);

                Consider(start);
                Consider(end);
            }
        }

        return best;
    }
}

/// <summary>Periodically evaluates time-of-day speed rules every 30 s and applies them to the global <see cref="SpeedLimiter"/>.</summary>
public sealed class SpeedRuleController : IDisposable
{
    readonly AppSettings _settings;
    readonly SpeedLimiter _limiter;
    readonly Func<DateTime> _clock;
    readonly Timer? _timer;
    readonly object _gate = new();

    bool _manualOverrideActive;
    bool _manualEnabled;
    int _manualLimitKBps;
    bool _wasRuleApplied;

    public DateTime? ManualOverrideUntil { get; private set; }
    public bool IsManualOverrideActive => _manualOverrideActive;
    public int? ActiveRuleLimitKBps { get; private set; }

    public SpeedRuleController(
        AppSettings settings,
        SpeedLimiter limiter,
        Func<DateTime>? clock = null,
        bool startTimer = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limiter);
        _settings = settings;
        _limiter = limiter;
        _clock = clock ?? (() => DateTime.Now);

        if (startTimer)
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    /// <summary>Overrides the time schedule manually until the next rule boundary.</summary>
    public void SetManualOverride(bool enabled, int? limitKBps = null)
    {
        lock (_gate)
        {
            var now = _clock();
            _manualOverrideActive = true;
            _manualEnabled = enabled;
            _manualLimitKBps = limitKBps ?? _settings.SpeedLimitKB;
            ManualOverrideUntil = SpeedRules.NextBoundary(_settings.SpeedRules, now);

            _limiter.Enabled = _manualEnabled;
            if (_manualLimitKBps > 0)
                _limiter.BytesPerSecond = _manualLimitKBps * 1024L;
        }
    }

    public void ClearManualOverride()
    {
        lock (_gate)
        {
            _manualOverrideActive = false;
            ManualOverrideUntil = null;
        }
        Tick();
    }

    public void OnSettingsChanged()
    {
        lock (_gate)
        {
            _manualOverrideActive = false;
            ManualOverrideUntil = null;
        }
        Tick();
    }

    public void Tick()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_manualOverrideActive)
            {
                if (ManualOverrideUntil is { } until && now >= until)
                {
                    _manualOverrideActive = false;
                    ManualOverrideUntil = null;
                }
                else
                {
                    _limiter.Enabled = _manualEnabled;
                    if (_manualLimitKBps > 0)
                        _limiter.BytesPerSecond = _manualLimitKBps * 1024L;
                    return;
                }
            }

            var evaluated = SpeedRules.Evaluate(_settings.SpeedRules, now);
            ActiveRuleLimitKBps = evaluated;

            if (evaluated != null)
            {
                _wasRuleApplied = true;
                if (evaluated.Value <= 0)
                {
                    _limiter.Enabled = false;
                }
                else
                {
                    _limiter.Enabled = true;
                    _limiter.BytesPerSecond = evaluated.Value * 1024L;
                }
            }
            else if (_wasRuleApplied)
            {
                _wasRuleApplied = false;
                _limiter.Enabled = _settings.SpeedLimitEnabled;
                _limiter.BytesPerSecond = Math.Max(1, _settings.SpeedLimitKB) * 1024L;
            }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
