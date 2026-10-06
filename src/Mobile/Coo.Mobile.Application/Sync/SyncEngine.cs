using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Sync;

/// <summary>What the sync line and the debug screen read (Spec 011 §2, §11).</summary>
/// <param name="LastSyncedAt">The phone's local time of the last successful cycle.</param>
/// <param name="Pending">Outbox records of the families that sync (Spec 011b §7.2, ruling 6).</param>
/// <param name="Removal">
/// A family the person was removed from, waiting for <see cref="SyncEngine.AcknowledgeRemovalAsync"/>: one at a time (ruling 23).
/// </param>
/// <param name="JoinReady">
/// Join-pending, consent given, and a pull of the account's household has completed: bring-over may be offered (Spec 011 §4).
/// </param>
/// <param name="ShownStopped">The shown family answered <c>not-a-member</c> and stopped until a wake (Spec 011b §9).</param>
public sealed record SyncStatus(
    CycleOutcome? Last, DateTimeOffset? LastSyncedAt, int Pending, bool IsRunning, string? LastError, HouseholdId? Removal, bool JoinReady = false,
    bool ShownStopped = false)
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
/// (<see cref="SyncCycle.NoProgressError"/>) stops the cycles: the status keeps it, and only a wake (app start, resume,
/// Sync now, a requested sync or an account change) tries again. A wake also lifts every family's <c>not-a-member</c> stop
/// (<see cref="SyncStops"/>) and refreshes the account's family list before the next cycle (Spec 011b §7.1, §9). After each
/// cycle a soft-locked shown family gives way to another (§8.2).
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
    private readonly Families _families;
    private readonly IFamilyRecords _records;
    private readonly SyncStops _stops;
    private readonly SyncScheduler _scheduler;
    private readonly Lock _statusLock = new();
    private SyncStatus _status = SyncStatus.Initial;
    private AccountState _lastAccount;

    // Stopped while _stoppedAtWake == _wakes; every wake moves _wakes, so a wake during the stopping cycle still counts.
    private long _wakes;
    private long _stoppedAtWake = -1;

    // Set by every wake (and for the first cycle): the next cycle refreshes the family list before it runs.
    private int _refreshFamilies = 1;

    // Moves when an acknowledged family has gone from the phone: a cycle that began before reports no removal (it may have
    // read that family before it went, review I1).
    private long _removalEpoch;

    // 1 while an acknowledgment runs: a second one (a double tap) does nothing.
    private int _acknowledging;

    // Moves with every account change, before its status update: a cycle that outlived the account it started under
    // reports nothing (its Removal or sync time belongs to that account).
    private long _accountEpoch;

    public SyncEngine(
        SyncCycle cycle, ISyncStore store, IHouseholdMoves moves, ISessionStore sessions, Account account, ConsentGate consent,
        LocalWrites writes, TimeProvider time, Families families, IFamilyRecords records, SyncStops stops,
        SyncOptions? options = null)
    {
        _cycle = cycle;
        _store = store;
        _moves = moves;
        _sessions = sessions;
        _account = account;
        _consent = consent;
        _writes = writes;
        _time = time;
        _families = families;
        _records = records;
        _stops = stops;
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
        // A cycle in flight would pull into the household being wiped. A cycle that starts after this wait is accepted
        // (a debug action: the worst case is repeating it).
        await _scheduler.WhenIdleAsync().WaitAsync(cancellationToken);
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

    /// <summary>
    /// The person pressed OK on the removal sheet (Spec 011b §8.2): the family goes from this phone (ADR 0002 §10), replaced
    /// by a new provisional family when it was the last one (§8.4), and the list is refreshed. Nothing is deleted before.
    /// The next cycle creates a replacement family on the server (§8.4) and reports the next removal, if any.
    /// </summary>
    public async Task AcknowledgeRemovalAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _acknowledging, 1, 0) != 0)
            return;
        try
        {
            if (Status.Removal is not { } household)
                return;
            await _families.RemoveFromPhoneAsync(household, cancellationToken);
            Interlocked.Increment(ref _removalEpoch);

            // A removal of another family reported meanwhile stays for its own sheet.
            Update(s => s.Removal == household ? s with { Removal = null, Last = null } : s);
        }
        finally
        {
            Interlocked.Exchange(ref _acknowledging, 0);
        }

        await RefreshFamiliesAsync(cancellationToken);
        Raise(DataChanged);
        RequestSync();
    }

    public void NotifyDataChanged() => Raise(DataChanged);

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

        var epoch = Interlocked.Read(ref _accountEpoch);
        var removalEpoch = Interlocked.Read(ref _removalEpoch);
        Update(s => s with { IsRunning = true });
        if (Interlocked.Exchange(ref _refreshFamilies, 0) == 1)
            await RefreshFamiliesAsync(cancellationToken);

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

        if (report.RefreshFamilies)
            await RefreshFamiliesAsync(cancellationToken);
        var switched = await HideSoftLockedAsync(cancellationToken);

        // Review M-b: the paused family is no longer shown; the next cycle speaks for the one that is.
        var rerunForShown = switched && report.Outcome == CycleOutcome.SoftLocked;
        var shownStopped = await ShownStoppedAsync(cancellationToken);

        var pending = await PendingAsync(cancellationToken);
        var joinReady = await JoinReadyAsync(cancellationToken);
        var now = _time.GetLocalNow();
        var signedOut = report.Outcome == CycleOutcome.NotSignedIn;
        var outlived = false;
        Update(s =>
        {
            // Checked under the status lock: an account change either lands before this (and the result is dropped) or
            // after it (and its own update replaces the result).
            if (Interlocked.Read(ref _accountEpoch) != epoch)
            {
                outlived = true;
                return s with { IsRunning = false };
            }

            if (rerunForShown)
                return s with { Pending = pending ?? s.Pending, IsRunning = false };

            var removal = report.Outcome == CycleOutcome.Removed && Interlocked.Read(ref _removalEpoch) == removalEpoch ? report.Household : null;
            return s with
            {
                Last = report.Outcome,
                LastSyncedAt = report.Outcome == CycleOutcome.Synced ? now : signedOut ? null : s.LastSyncedAt,
                Pending = pending ?? s.Pending,
                IsRunning = false,
                LastError = report.Error,
                Removal = removal ?? (signedOut ? null : s.Removal),
                JoinReady = joinReady ?? (!signedOut && s.JoinReady),
                ShownStopped = shownStopped,
            };
        });
        if (report.DataChanged || switched)
            Raise(DataChanged);

        // The account change requested its own cycle; this one must not back off or stop and swallow it.
        if (outlived)
            return true;

        // Marks the running cycle dirty: the scheduler runs the next one as soon as this one returns.
        if (report.RunAgain || rerunForShown)
            _scheduler.Request(SyncTrigger.Manual);
        if (rerunForShown)
            return true;

        if (report is { Outcome: CycleOutcome.Failed, Error: SyncCycle.NoProgressError })
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
            return await _records.CountPendingAsync(await _families.SyncingHouseholdsAsync(cancellationToken), cancellationToken);
        }
#pragma warning disable CA1031 // The count is for the sync line only; the last known one stays.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private async Task RefreshFamiliesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _families.RefreshAsync(cancellationToken);
        }
#pragma warning disable CA1031 // The list is refreshed again at the next wake; a failure never stops a cycle.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task<bool> HideSoftLockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _families.HideSoftLockedAsync(cancellationToken);
        }
#pragma warning disable CA1031 // The next cycle tries again; the shown family stays shown meanwhile.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task<bool> ShownStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return _stops.IsStopped(await _moves.ActiveHouseholdAsync(cancellationToken));
        }
#pragma warning disable CA1031 // For the sync line only.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task ClearFamiliesAsync()
    {
        try
        {
            await _families.ClearAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Fire-and-forget: the next sign-in replaces the list anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>For the sync line and the prompter (final review C1): null when it could not be read; the last value stays.</summary>
    private async Task<bool?> JoinReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _sessions.GetAsync(cancellationToken) is { JoinPending: true } session
                && await _consent.CachedAsync(session.Profile.UserId, cancellationToken) == ConsentState.Given
                && await _store.HasPulledAsync(session.AccountHousehold, cancellationToken);
        }
#pragma warning disable CA1031 // A store failure: the line keeps what it showed; the next cycle reads it again.
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
            var pending = await PendingAsync(CancellationToken.None);
            var joinReady = await JoinReadyAsync(CancellationToken.None);
            Update(s => s with { Pending = pending ?? s.Pending, JoinReady = joinReady ?? s.JoinReady });
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
        Interlocked.Increment(ref _accountEpoch);
        Wake();
        if (now is SignedOut)
        {
            Update(_ => SyncStatus.Initial with { Last = CycleOutcome.NotSignedIn });
            _ = ClearFamiliesAsync();

            // A cycle still in flight drops its result (the epoch moved); this one, signed out and without IO, settles it.
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

    private void Wake()
    {
        Interlocked.Increment(ref _wakes);
        _stops.Clear();
        Interlocked.Exchange(ref _refreshFamilies, 1);
    }

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
