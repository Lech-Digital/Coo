using System.Security.Cryptography;

namespace Coo.Mobile.Application.Sync;

public enum SyncTrigger
{
    Start,
    Resume,
    LocalWrite,
    ConnectivityRegained,
    Background,
    Periodic,
    Manual,
}

/// <summary>
/// When cycles run (Spec 011 §6.3, ADR 0002 §7): one at a time; a trigger during a cycle marks it dirty and one more runs
/// after it (coalesced, never queued); local writes are debounced by a second; every 30 seconds in the foreground; on the
/// way to the background, one more cycle after any backoff. A failed cycle backs off from 2 s, doubling to 5 minutes, jittered ×[0.5, 1];
/// during a backoff only Start, Resume, connectivity regained and Manual run at once (they reset it, ruling 12).
/// Nothing escapes: a cycle that throws is a failure.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ForegroundInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<bool>> _cycle;
    private readonly TimeProvider _time;
    private readonly Func<double> _jitter;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _idle = CompletedIdle();
    private ITimer? _debounce;
    private ITimer? _backoff;
    private ITimer? _periodic;

    // Each timer's identity, passed as its state: a callback a real timer had already queued before it was replaced or
    // disposed compares unequal and does nothing.
    private object? _debounceTag;
    private object? _backoffTag;
    private object? _periodicTag;
    private bool _running;
    private bool _again;
    private bool _againAtOnce;
    private bool _waitingForBackoff;
    private bool _foreground;
    private bool _disposed;
    private int _failures;

    /// <param name="cycle">Runs one cycle; false means "failed, back off".</param>
    /// <param name="jitter">A factor in [0.5, 1]; the default draws it from <see cref="RandomNumberGenerator"/>.</param>
    public SyncScheduler(Func<CancellationToken, Task<bool>> cycle, TimeProvider time, Func<double>? jitter = null)
    {
        _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _jitter = jitter ?? (() => 0.5 + (RandomNumberGenerator.GetInt32(0, 501) / 1000.0));
    }

    public int Failures
    {
        get
        {
            lock (_gate)
                return _failures;
        }
    }

    public void Request(SyncTrigger trigger)
    {
        bool start;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (trigger == SyncTrigger.LocalWrite)
            {
                _debounce?.Dispose();
                _debounceTag = new object();
                _debounce = _time.CreateTimer(tag => Fire(tag, () => _debounceTag), _debounceTag, Debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            start = Arm(resetBackoff: trigger is SyncTrigger.Start or SyncTrigger.Resume or SyncTrigger.ConnectivityRegained or SyncTrigger.Manual);
        }

        if (start)
            Launch();
    }

    public void SetForeground(bool foreground)
    {
        var start = false;
        lock (_gate)
        {
            if (_disposed || _foreground == foreground)
                return;
            _foreground = foreground;
            _periodic?.Dispose();
            _periodicTag = foreground ? new object() : null;
            _periodic = foreground
                ? _time.CreateTimer(tag => Fire(tag, () => _periodicTag), _periodicTag, ForegroundInterval, ForegroundInterval)
                : null;
            if (!foreground)
                start = Arm(resetBackoff: false);
        }

        if (start)
            Launch();
    }

    /// <summary>A cycle now (Sync now, after consent or a bring-over); completes when the cycles it caused have ended.</summary>
    public async Task RunNowAsync(CancellationToken cancellationToken)
    {
        Request(SyncTrigger.Manual);
        await WhenIdleAsync().WaitAsync(cancellationToken);
    }

    /// <summary>Completes when no cycle is running or about to run. For tests and <see cref="RunNowAsync"/>.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _idle.Task;
    }

    /// <summary>Stops the timers and completes <see cref="WhenIdleAsync"/> at once, even while a cycle is still unwinding.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounce?.Dispose();
            _backoff?.Dispose();
            _periodic?.Dispose();
            _idle.TrySetResult();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }

    // A timer callback: nothing may escape it. A stale one (its timer replaced or disposed since) does nothing.
    private void Fire(object? tag, Func<object?> current, bool endsBackoff = false)
    {
        try
        {
            bool start;
            lock (_gate)
            {
                if (_disposed || tag is null || !ReferenceEquals(tag, current()))
                    return;
                if (endsBackoff)
                {
                    _waitingForBackoff = false;
                    _backoffTag = null;
                }

                start = Arm(resetBackoff: false);
            }

            if (start)
                Launch();
        }
#pragma warning disable CA1031 // A timer callback that throws would crash the app; the next trigger tries again.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Under the lock. True when the caller must start the loop.</summary>
    private bool Arm(bool resetBackoff)
    {
        if (resetBackoff)
        {
            _failures = 0;
            _waitingForBackoff = false;
            _backoff?.Dispose();
            _backoff = null;
            _backoffTag = null;
        }

        if (_waitingForBackoff)
            return false;
        if (_running)
        {
            _again = true;

            // Sync now or connectivity during a cycle: the next cycle runs at once even if this one fails.
            _againAtOnce |= resetBackoff;
            return false;
        }

        _running = true;
        if (_idle.Task.IsCompleted)
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    // Started outside the lock and off the caller's thread: a cycle never runs under the lock or inside a timer callback.
    private void Launch() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (true)
        {
            bool succeeded;
            try
            {
                succeeded = await _cycle(_lifetime.Token);
            }
#pragma warning disable CA1031 // Fire-and-forget: anything a cycle throws is a failed cycle, never a crash.
            catch (Exception)
#pragma warning restore CA1031
            {
                succeeded = false;
            }

            lock (_gate)
            {
                if (succeeded)
                {
                    _failures = 0;
                }
                else
                {
                    _failures++;
                    if (!_againAtOnce)
                    {
                        _again = false;
                        ArmBackoff();
                    }
                }

                if (_again && !_disposed)
                {
                    _again = false;
                    _againAtOnce = false;
                    continue;
                }

                _againAtOnce = false;

                _running = false;
                _idle.TrySetResult();
                return;
            }
        }
    }

    /// <summary>Under the lock, in the loop's bookkeeping: a jitter source that throws or returns nonsense must not stop the loop.</summary>
    private double Jitter()
    {
        try
        {
            var factor = _jitter();
            return double.IsFinite(factor) ? Math.Clamp(factor, 0.5, 1.0) : 1.0;
        }
#pragma warning disable CA1031 // The loop must reach its bookkeeping; a full backoff is the safe default.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 1.0;
        }
    }

    /// <summary>Under the lock.</summary>
    private void ArmBackoff()
    {
        if (_disposed)
            return;
        var doubled = FirstBackoff.Ticks << Math.Min(_failures - 1, 20);
        var delay = TimeSpan.FromTicks((long)(Math.Min(MaxBackoff.Ticks, doubled) * Jitter()));
        _waitingForBackoff = true;
        _backoff?.Dispose();
        _backoffTag = new object();
        _backoff = _time.CreateTimer(tag => Fire(tag, () => _backoffTag, endsBackoff: true), _backoffTag, delay, Timeout.InfiniteTimeSpan);
    }
}
