namespace Golan.Gdm.Core;

/// <summary>Starts and stops queues at their scheduled times (IDM "Scheduler"). Call <see cref="Tick"/> every few seconds.</summary>
public sealed class QueueScheduler(DownloadManager manager)
{
    static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public void StartupQueues()
    {
        foreach (var q in manager.Settings.Queues.Where(q => q.StartOnStartup && q.ScheduleEnabled))
            manager.StartQueue(q.Id);
    }

    public void Tick(DateTime now)
    {
        foreach (var q in manager.Settings.Queues.Where(q => q.ScheduleEnabled))
        {
            if (!manager.IsQueueRunning(q.Id) && ShouldStart(q, now)) manager.StartQueue(q.Id);
            else if (manager.IsQueueRunning(q.Id) && ShouldStop(q, now)) manager.StopQueue(q.Id);
        }
    }

    /// <summary>Next start time (for the UI), or null.</summary>
    public static DateTime? NextStart(QueueDef q, DateTime now)
    {
        if (!q.ScheduleEnabled || !TimeSpan.TryParse(q.StartTime, out var t)) return null;
        if (!q.Daily) return q.OnceDate is { } d && d.Date + t > now ? d.Date + t : null;
        for (int i = 0; i < 8; i++)
        {
            var day = now.Date.AddDays(i);
            if (q.Days.Contains(day.DayOfWeek) && day + t > now) return day + t;
        }
        return null;
    }

    public static bool ShouldStart(QueueDef q, DateTime now)
    {
        if (!q.ScheduleEnabled || !TimeSpan.TryParse(q.StartTime, out var t)) return false;
        TimeSpan s = default;
        bool overnight = q.StopEnabled && TimeSpan.TryParse(q.StopTime, out s) && s <= t;
        DateTime due;
        if (q.Daily)
        {
            var anchorDate = overnight && now.TimeOfDay < s ? now.Date.AddDays(-1) : now.Date;
            if (!q.Days.Contains(anchorDate.DayOfWeek)) return false;
            due = anchorDate + t;
        }
        else
        {
            if (q.OnceDate is not { } d) return false;
            due = d.Date + t;
            var maxDay = overnight ? d.Date.AddDays(1) : d.Date;
            if (now.Date > maxDay) return false;
            if (overnight && now.Date == maxDay && now.TimeOfDay >= s) return false;
        }

        if (PastStop(q, now)) return false;

        // First start of the day / the one-time start.
        if (now >= due && now - due < Window && (q.LastStart == null || q.LastStart < due)) return true;

        // "Start again every H:M" after the first start of the day.
        if (q.RepeatEnabled && now >= due)
        {
            var every = TimeSpan.FromHours(q.RepeatHours) + TimeSpan.FromMinutes(q.RepeatMinutes);
            var last = q.LastStart ?? due;
            if (every >= TimeSpan.FromMinutes(1) && now - last >= every) return true;
        }
        return false;
    }

    public static bool ShouldStop(QueueDef q, DateTime now)
    {
        if (!q.StopEnabled || !TimeSpan.TryParse(q.StopTime, out var s)) return false;
        var stop = now.Date + s;
        return now >= stop && now - stop < Window && (q.LastStop == null || q.LastStop < stop) && (q.LastStart == null || q.LastStart < stop);
    }

    static bool PastStop(QueueDef q, DateTime now)
        => q.StopEnabled && TimeSpan.TryParse(q.StopTime, out var s) && TimeSpan.TryParse(q.StartTime, out var t)
           && (s > t ? now.TimeOfDay >= s || now.TimeOfDay < t : now.TimeOfDay >= s && now.TimeOfDay < t);
}
