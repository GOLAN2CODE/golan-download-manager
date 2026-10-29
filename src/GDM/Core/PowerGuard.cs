using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Golan.Gdm.Core;

public enum PowerGuardState
{
    Idle,
    Holding,
    Releasing,
}

/// <summary>
/// F1: Keeps the PC (and optionally the display) awake while at least one download or queue is active.
/// SetThreadExecutionState is per-thread on Windows, so all native calls are dispatched through a
/// BlockingCollection&lt;uint&gt; to a single dedicated background thread.
/// </summary>
public sealed class PowerGuard : IDisposable
{
    public const uint ES_CONTINUOUS = 0x80000000u;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001u;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002u;

    public static readonly TimeSpan ReleaseDebounce = TimeSpan.FromSeconds(60);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern uint SetThreadExecutionState(uint esFlags);

    readonly object _sync = new();
    readonly Func<DateTime> _clock;
    readonly Action<uint> _native;
    readonly BlockingCollection<uint> _queue = new();
    readonly AutoResetEvent _processed = new(false);
    readonly Thread _thread;

    DateTime? _releaseStartedAt;
    uint _lastQueuedFlags = ES_CONTINUOUS;
    bool _disposed;

    public bool KeepSystemAwake { get; set; } = true;
    public bool KeepDisplayOn { get; set; }
    public PowerGuardState State { get; private set; } = PowerGuardState.Idle;

    public PowerGuard(Func<DateTime>? clock = null, Action<uint>? native = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _native = native ?? (flags => _ = SetThreadExecutionState(flags));
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Golan.PowerGuard",
        };
        _thread.Start();
    }

    public void Update(bool anyActive)
    {
        lock (_sync)
        {
            if (_disposed) return;

            if (!KeepSystemAwake)
            {
                if (State != PowerGuardState.Idle || _lastQueuedFlags != ES_CONTINUOUS)
                {
                    State = PowerGuardState.Idle;
                    _releaseStartedAt = null;
                    EnqueueLocked(ES_CONTINUOUS);
                }
                return;
            }

            uint activeFlags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | (KeepDisplayOn ? ES_DISPLAY_REQUIRED : 0u);

            if (anyActive)
            {
                State = PowerGuardState.Holding;
                _releaseStartedAt = null;
                EnqueueLocked(activeFlags);
                return;
            }

            if (State == PowerGuardState.Holding)
            {
                State = PowerGuardState.Releasing;
                _releaseStartedAt = _clock();
                return;
            }

            if (State == PowerGuardState.Releasing)
            {
                var now = _clock();
                var started = _releaseStartedAt ?? now;
                if (now - started >= ReleaseDebounce)
                {
                    State = PowerGuardState.Idle;
                    _releaseStartedAt = null;
                    EnqueueLocked(ES_CONTINUOUS);
                }
            }
        }
    }

    void EnqueueLocked(uint flags)
    {
        if (_lastQueuedFlags == flags) return;
        _lastQueuedFlags = flags;
        _processed.Reset();
        _queue.Add(flags);
        _processed.WaitOne(2000);
    }

    void WorkerLoop()
    {
        uint lastApplied = ES_CONTINUOUS;
        try
        {
            foreach (var flags in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (lastApplied != flags)
                    {
                        lastApplied = flags;
                        _native(flags);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("PowerGuard native call failed: " + ex.Message);
                }
                finally
                {
                    try { _processed.Set(); }
                    catch (ObjectDisposedException) { /* event already disposed during shutdown */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("PowerGuard worker loop error: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            State = PowerGuardState.Idle;
            _releaseStartedAt = null;
            if (_lastQueuedFlags != ES_CONTINUOUS)
            {
                _lastQueuedFlags = ES_CONTINUOUS;
                _queue.Add(ES_CONTINUOUS);
                _processed.WaitOne(2000);
            }
            _queue.CompleteAdding();
        }

        try { _thread.Join(TimeSpan.FromSeconds(5)); }
        catch (ThreadStateException) { /* worker thread not running */ }
        try { _queue.Dispose(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        try { _processed.Dispose(); }
        catch (ObjectDisposedException) { /* already disposed */ }
    }
}
