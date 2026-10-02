using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

/// <summary>What the sync line and the debug screen read (Spec 011 §2, §11).</summary>
/// <param name="LastSyncedAt">The phone's local time of the last successful cycle.</param>
/// <param name="Pending">Outbox rows of the account's household.</param>
/// <param name="Removal">A household the person was removed from, waiting for <see cref="SyncEngine.AcknowledgeRemovalAsync"/>.</param>
public sealed record SyncStatus(CycleOutcome? Last, DateTimeOffset? LastSyncedAt, int Pending, bool IsRunning, string? LastError, HouseholdId? Removal)
{
    public static SyncStatus Initial { get; } = new(null, null, 0, false, null, null);
}

public interface ISyncStatus
{
    SyncStatus Status { get; }

    event EventHandler? StatusChanged;
}

/// <summary>For tests: a fixed jitter makes backoff exact.</summary>
public sealed record SyncOptions(Func<double> Jitter);

/// <summary>
/// The phone's sync, as the app sees it (Spec 011 §6.3): it wires the triggers (start, resume, local writes, connectivity,
/// background, every 30 s in the foreground) to the scheduler, runs <see cref="SyncCycle"/>, and keeps the status the
/// sync line reads. Events are raised from background threads; handlers dispatch to the UI themselves. Each handler is
/// called inside a catch-all: one that throws cannot break the engine or the other handlers.
/// A cycle that throws (a store error) is a failed cycle and backs off. A failure that retrying cannot fix
/// (<see cref="SyncCycle.HouseholdMismatchError"/>, <see cref="SyncCycle.NoProgressError"/>) stops the cycles: the
/// status keeps it, and only app start, resume, Sync now or an account change tries again.
/// </summary>
public sealed class SyncEngine : ISyncStatus, ISyncControl, IDisposable
{
    private readonly SyncCycle _cycle;
    private readonly ISyncStore _store;
    private readonly IHouseholdMoves _moves;
    private readonly ISessionStore _sessions;
    private readonly Account _account;
    private readonly ConsentGate _consent;
    private readonly LocalWrites _writes;
    private readonly TimeProvider _time;
    private readonly SyncScheduler _scheduler;
    private readonly Lock _statusLock = new();
    private SyncStatus _status = SyncStatus.Initial;
    private AccountState _lastAccount;

    // Stopped while _stoppedAtWake == _wakes; every wake moves _wakes, so a wake during the stopping cycle still counts.
    private long _wakes;
    private long _stoppedAtWake = -1;

    public SyncEngine(
        SyncCycle cycle, ISyncStore store, IHouseholdMoves moves, ISessionStore sessions, Account account, ConsentGate consent,
        LocalWrites writes, TimeProvider time, SyncOptions? options = null)
    {
        _cycle = cycle;
        _store = store;
        _moves = moves;
        _sessions = sessions;
        _account = account;
        _consent = consent;
        _writes = writes;
        _time = time;
        _scheduler = new SyncScheduler(RunCycleAsync, time, options?.Jitter);
        _lastAccount = account.State;
        account.Changed += OnAccountChanged;
        writes.Written += OnLocalWrite;
    }

    public event EventHandler? StatusChanged;

    /// <summary>Local data changed under the UI (pulled records, a move, a removal): pages refresh.</summary>
    public event EventHandler? DataChanged;

    public SyncStatus Status
    {
        get
        {
            lock (_statusLock)
                return _status;
        }
    }

    /// <summary>The app's first page is up: foreground, and a first cycle.</summary>
    public void Start()
    {
        Wake();
        _scheduler.SetForeground(true);
        _scheduler.Request(SyncTrigger.Start);
        _ = RefreshPendingAsync();
    }

    public void Resumed()
    {
        Wake();
        _scheduler.SetForeground(true);
        _scheduler.Request(SyncTrigger.Resume);
    }

    public void Backgrounded() => _scheduler.SetForeground(false);

    public void ConnectivityRegained() => _scheduler.Request(SyncTrigger.ConnectivityRegained);

    public void RequestSync()
    {
        Wake();
        _scheduler.Request(SyncTrigger.Manual);
    }

    public Task SyncNowAsync(CancellationToken cancellationToken)
    {
        Wake();
        return _scheduler.RunNowAsync(cancellationToken);
    }

    /// <summary>The debug screen's Fresh bootstrap (Spec 011 §11): only with an empty outbox; the log comes back from cursor 0.</summary>
    public async Task<bool> FreshBootstrapAsync(CancellationToken cancellationToken)
    {
        if (await _sessions.GetAsync(cancellationToken) is not { JoinPending: false } session)
            return false;
        if (!await _moves.FreshBootstrapAsync(session.AccountHousehold, cancellationToken))
            return false;
        Raise(DataChanged);
        await SyncNowAsync(cancellationToken);
        return true;
    }

    public async Task<SyncCounts?> CountsAsync(CancellationToken cancellationToken) =>
        await _sessions.GetAsync(cancellationToken) is { } session ? await _store.CountsAsync(session.AccountHousehold, cancellationToken) : null;

    /// <summary>The person was told they were removed (11b's sheet): the household goes from this phone (ADR 0002 §10).</summary>
    public async Task AcknowledgeRemovalAsync(CancellationToken cancellationToken)
    {
        if (Status.Removal is not { } household)
            return;
        await _moves.DeleteHouseholdAsync(household, cancellationToken);
        Update(s => s with { Removal = null, Last = null });
        Raise(DataChanged);
    }

    /// <summary>For tests: completes when no cycle is running or about to run.</summary>
    internal Task WhenIdleAsync() => _scheduler.WhenIdleAsync();

    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        _writes.Written -= OnLocalWrite;
        _scheduler.Dispose();
    }

    private async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
    {
        var wake = Interlocked.Read(ref _wakes);
        if (Interlocked.Read(ref _stoppedAtWake) == wake)
            return true;

        Update(s => s with { IsRunning = true });
        CycleReport report;
        try
        {
            report = await _cycle.RunAsync(cancellationToken);
        }
#pragma warning disable CA1031 // A store failure (SQLite) is a failed cycle; the scheduler backs off.
        catch (Exception e)
#pragma warning restore CA1031
        {
            report = new CycleReport(CycleOutcome.Failed, Error: e.GetType().Name);
        }

        var pending = await PendingAsync(cancellationToken);
        var now = _time.GetLocalNow();
        Update(s => s with
        {
            Last = report.Outcome,
            LastSyncedAt = report.Outcome == CycleOutcome.Synced ? now : s.LastSyncedAt,
            Pending = pending ?? s.Pending,
            IsRunning = false,
            LastError = report.Error,
            Removal = report.Outcome == CycleOutcome.Removed ? report.Household : s.Removal,
        });
        if (report.DataChanged)
            Raise(DataChanged);

        if (report is { Outcome: CycleOutcome.Failed, Error: SyncCycle.HouseholdMismatchError or SyncCycle.NoProgressError })
        {
            // Not a connection problem: backing off would only repeat it. Stopped until something wakes the engine.
            Interlocked.Exchange(ref _stoppedAtWake, wake);
            return true;
        }

        return report.Outcome is not (CycleOutcome.Failed or CycleOutcome.ClientTooOld or CycleOutcome.SoftLocked or CycleOutcome.Removed);
    }

    private async Task<int?> PendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await CountsAsync(cancellationToken))?.Outbox;
        }
#pragma warning disable CA1031 // The count is for the sync line only; the last known one stays.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private async Task RefreshPendingAsync()
    {
        try
        {
            if (await PendingAsync(CancellationToken.None) is { } pending)
                Update(s => s with { Pending = pending });
        }
#pragma warning disable CA1031 // Fire-and-forget: the count is for the sync line only; the next cycle refreshes it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnLocalWrite(object? sender, EventArgs e)
    {
        _scheduler.Request(SyncTrigger.LocalWrite);
        _ = RefreshPendingAsync();
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        var now = _account.State;
        var before = Interlocked.Exchange(ref _lastAccount, now);
        Wake();
        if (now is SignedOut)
        {
            Update(_ => SyncStatus.Initial with { Last = CycleOutcome.NotSignedIn });

            // A cycle still in flight may report after this; the next one says "not signed in" again, without any IO.
            _scheduler.Request(SyncTrigger.Manual);
            return;
        }

        if (before is SignedOut)
        {
            _ = ResetDeclineThenStartAsync();
            return;
        }

        // Join-pending to signed in: the bring-over moved this phone into the family's log; the pages show it now.
        if (before is JoinPending && now is SignedIn)
            Raise(DataChanged);
        _scheduler.Request(SyncTrigger.Manual);
    }

    private async Task ResetDeclineThenStartAsync()
    {
        try
        {
            if (await _sessions.GetAsync(CancellationToken.None) is { } session)
                await _consent.ResetDeclinedAsync(session.Profile.UserId, CancellationToken.None);
        }
#pragma warning disable CA1031 // Fire-and-forget: the next cycle reads the consent again anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            _scheduler.Request(SyncTrigger.Start);
        }
#pragma warning disable CA1031 // Fire-and-forget: nothing here may escape into an unobserved task.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void Wake() => Interlocked.Increment(ref _wakes);

    private void Update(Func<SyncStatus, SyncStatus> change)
    {
        lock (_statusLock)
            _status = change(_status);
        Raise(StatusChanged);
    }

    /// <summary>Each handler on its own: one that throws is dropped and the others still run.</summary>
    private void Raise(EventHandler? handlers)
    {
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A view model's bug must not stop sync or the other handlers.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
