using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §6.3, ADR 0002 §7: one cycle at a time, triggers coalesced, backoff with jitter up to 5 minutes.</summary>
public sealed class SyncSchedulerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly SyncScheduler _scheduler;
    private TaskCompletionSource _gate = Released();
    private int _runs;
    private int _running;
    private int _maxRunning;
    private Func<bool> _outcome = () => true;

    public SyncSchedulerTests()
    {
        _timers = new RecordingTime(_time);
        _scheduler = new SyncScheduler(CycleAsync, _timers, jitter: () => 1.0);
    }

    private readonly RecordingTime _timers;

    public void Dispose() => _scheduler.Dispose();

    private static TaskCompletionSource Released()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private async Task<bool> CycleAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        var now = Interlocked.Increment(ref _running);
        InterlockedMax(ref _maxRunning, now);
        try
        {
            await _gate.Task;
            return _outcome();
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [Fact]
    public async Task Request_ManyTriggersDuringACycle_RunOneMoreCycleNotOneEach()
    {
        // Arrange
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.Request(SyncTrigger.Start);

        // Act
        _scheduler.Request(SyncTrigger.Periodic);
        _scheduler.Request(SyncTrigger.Resume);
        _scheduler.Request(SyncTrigger.Manual);
        var idle = _scheduler.WhenIdleAsync();
        _gate.SetResult();
        await idle;

        // Assert
        _runs.Should().Be(2);
        _maxRunning.Should().Be(1);
    }

    [Fact]
    public async Task Request_LocalWritesInABurst_AreDebouncedIntoOneCycle()
    {
        // Act
        for (var i = 0; i < 5; i++)
        {
            _scheduler.Request(SyncTrigger.LocalWrite);
            _time.Advance(TimeSpan.FromMilliseconds(300));
        }

        var before = _runs;
        _time.Advance(SyncScheduler.Debounce);
        await _scheduler.WhenIdleAsync();

        // Assert
        before.Should().Be(0);
        _runs.Should().Be(1);
    }

    [Fact]
    public async Task Request_PeriodicDuringBackoff_WaitsForTheBackoff()
    {
        // Arrange: offline (Review Focus 2).
        _outcome = () => false;
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();

        // Act
        _scheduler.Request(SyncTrigger.Periodic);
        _scheduler.Request(SyncTrigger.LocalWrite);
        _time.Advance(SyncScheduler.Debounce);
        await _scheduler.WhenIdleAsync();
        var duringBackoff = _runs;
        _time.Advance(SyncScheduler.FirstBackoff);
        await _scheduler.WhenIdleAsync();

        // Assert
        duringBackoff.Should().Be(1);
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task Request_RepeatedFailures_DoubleTheBackoffUpToFiveMinutes()
    {
        // Arrange
        _outcome = () => false;
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();
        var delays = new List<TimeSpan>();

        // Act: measure each wait by advancing a second at a time until the next cycle starts.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var runs = _runs;
            var waited = TimeSpan.Zero;
            while (_runs == runs)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                waited += TimeSpan.FromSeconds(1);
                await _scheduler.WhenIdleAsync();
            }

            delays.Add(waited);
        }

        // Assert (jitter fixed at 1.0)
        delays.Take(5).Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32));
        delays.Max().Should().Be(SyncScheduler.MaxBackoff);
    }

    [Fact]
    public async Task Request_ConnectivityRegained_ResetsTheBackoffAndRuns()
    {
        // Arrange
        _outcome = () => false;
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();
        _outcome = () => true;

        // Act
        _scheduler.Request(SyncTrigger.ConnectivityRegained);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(2);
        _scheduler.Failures.Should().Be(0);
    }

    [Fact]
    public async Task SetForeground_ThirtySecondsPass_RunsACycleEachTime()
    {
        // Arrange
        _scheduler.SetForeground(true);

        // Act
        _time.Advance(SyncScheduler.ForegroundInterval);
        await _scheduler.WhenIdleAsync();
        _time.Advance(SyncScheduler.ForegroundInterval);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task SetForeground_GoingToTheBackground_RunsOneLastCycleAndStopsTheTimer()
    {
        // Arrange
        _scheduler.SetForeground(true);

        // Act
        _scheduler.SetForeground(false);
        await _scheduler.WhenIdleAsync();
        _time.Advance(SyncScheduler.ForegroundInterval * 3);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(1);
    }

    [Fact]
    public async Task Request_CycleThrows_CountsAsAFailureAndTheSchedulerKeepsWorking()
    {
        // Arrange: nothing may escape the fire-and-forget loop.
        _outcome = () => throw new InvalidOperationException("boom");
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();
        _outcome = () => true;

        // Act
        _scheduler.Request(SyncTrigger.Manual);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(2);
        _scheduler.Failures.Should().Be(0);
    }

    [Fact]
    public async Task RunNowAsync_WhileACycleRuns_CompletesAfterTheNextOne()
    {
        // Arrange
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.Request(SyncTrigger.Start);

        // Act
        var now = _scheduler.RunNowAsync(CancellationToken.None);
        var completedEarly = now.IsCompleted;
        _gate.SetResult();
        await now;

        // Assert
        completedEarly.Should().BeFalse();
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task Request_FailureWithAJitterSourceThatThrows_StillEndsTheCycleAndBacksOff()
    {
        // Arrange: the loop's bookkeeping must always run, or the scheduler would stay "running" for ever.
        using var scheduler = new SyncScheduler(CycleAsync, _time, jitter: () => throw new InvalidOperationException("entropy"));
        _outcome = () => false;

        // Act
        scheduler.Request(SyncTrigger.Start);
        await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var failures = scheduler.Failures;
        _time.Advance(SyncScheduler.FirstBackoff);
        await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert (the timeouts only bound a broken build; a full backoff is the fallback)
        failures.Should().Be(1);
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task RunNowAsync_DuringACycleThatFails_RunsTheRequestedCycleWithoutWaitingForTheBackoff()
    {
        // Arrange: ruling 12, Sync now runs at once, even when it lands during a failing cycle.
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _outcome = () => Volatile.Read(ref _runs) > 1;
        _scheduler.Request(SyncTrigger.Start);

        // Act
        var now = _scheduler.RunNowAsync(CancellationToken.None);
        _gate.SetResult();
        await now.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert (no time passed: no backoff was waited for)
        _runs.Should().Be(2);
        _scheduler.Failures.Should().Be(0);
    }

    [Fact]
    public async Task Request_StaleBackoffTimerFiresAfterANewerBackoff_IsIgnored()
    {
        // Arrange: two failures, the second with its own backoff; the first backoff's callback was already queued.
        _outcome = () => false;
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();
        var first = _timers.Created[^1];
        _scheduler.Request(SyncTrigger.Manual);
        await _scheduler.WhenIdleAsync();

        // Act
        first.Callback(first.State);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task SetForeground_StalePeriodicTimerFiresAfterGoingToTheBackground_IsIgnored()
    {
        // Arrange
        _scheduler.SetForeground(true);
        var periodic = _timers.Created[^1];
        _scheduler.SetForeground(false);
        await _scheduler.WhenIdleAsync();

        // Act
        periodic.Callback(periodic.State);
        await _scheduler.WhenIdleAsync();

        // Assert: only the last cycle on the way to the background.
        _runs.Should().Be(1);
    }

    /// <summary>The fake clock, keeping each timer's callback so a test can play one that a real timer had already queued.</summary>
    private sealed class RecordingTime(FakeTimeProvider inner) : TimeProvider
    {
        public List<(TimerCallback Callback, object? State)> Created { get; } = [];

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Created)
                Created.Add((callback, state));
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }
}
