using Coo.Domain.Common;

namespace Coo.Mobile.Application.Sync;

/// <summary><c>/api/sync</c> and <c>/api/households/{id}/membership</c>, through the refreshing handler (Spec 011 §7.1).</summary>
public interface ISyncApi
{
    /// <summary>Throws <see cref="SyncUnavailableException"/> when nothing was decided, <see cref="SyncRefusedException"/> for a refusal.</summary>
    Task<IReadOnlyList<PushResult>> PushAsync(HouseholdId household, DeviceId device, IReadOnlyList<OutgoingChange> changes, CancellationToken cancellationToken);

    Task<PulledPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, CancellationToken cancellationToken);

    /// <summary>Never throws: anything but a clear answer is <see cref="MembershipStatus.Unknown"/>.</summary>
    Task<MembershipStatus> GetMembershipAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary><c>POST /api/consents</c> for the current wording (Spec 011 §3).</summary>
public interface IConsentApi
{
    Task RecordAsync(string appVersion, string language, CancellationToken cancellationToken);
}

/// <summary>The outbox, the pulled records and the cursors on this phone (Spec 011 §6).</summary>
public interface ISyncStore
{
    /// <summary>In one transaction, <c>this-device</c> becomes <c>user:&lt;id&gt;</c> in the household's entries (Spec 011 §5).</summary>
    Task PrepareForPushAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);

    /// <summary>Oldest first, at most <paramref name="max"/> records of this household, one change each.</summary>
    Task<IReadOnlyList<OutgoingChange>> NextBatchAsync(HouseholdId household, int max, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: each final result deletes its record's outbox rows up to the change; <c>rejected</c> keeps the
    /// record and lists it; <c>applied</c> and <c>superseded</c> clear an earlier rejection. Changes without a result stay.
    /// </summary>
    Task ApplyResultsAsync(
        HouseholdId household, IReadOnlyList<OutgoingChange> changes, IReadOnlyList<PushResult> results, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The household's cursor. With none yet, a household this phone already holds records of starts with head 0 (ruling 20).</summary>
    Task<SyncCursor> GetCursorAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>A pull of the household has completed: it has a cursor and is not part-way through a bootstrap.</summary>
    Task<bool> HasPulledAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>In one transaction: each record (unless a newer local change is pending, <see cref="PullRule"/>), and the new cursor.</summary>
    Task ApplyPageAsync(HouseholdId household, PulledPage page, CancellationToken cancellationToken);

    Task<SyncCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncRejection>> ListRejectionsAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary>Moving records between households on this phone (Spec 011 §4), each in one SQLite transaction.</summary>
public interface IHouseholdMoves
{
    /// <summary>
    /// The household the UI shows; names the first live one as active when none is named yet. A named household without
    /// settings is read past (the first live one is returned) but never replaced.
    /// </summary>
    Task<HouseholdId> ActiveHouseholdAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>created</c>: every record moves to the account's empty household, which becomes active. A household that has ever
    /// synced is never moved: nothing moves, the target becomes active, and the result is false. A target becoming active
    /// without settings gets a copy of the source's (with an outbox row), never its babies.
    /// </summary>
    Task<bool> MoveAllAsync(HouseholdId source, HouseholdId target, UserId user, CancellationToken cancellationToken);

    /// <summary>
    /// True when the household has a cursor, or holds a record with neither a pending outbox row nor a rejection: it was on a
    /// server once, so it is never moved (Spec 011 §4).
    /// </summary>
    Task<bool> HasEverSyncedAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Names the household active. Without settings of its own, it gets a copy of the shown household's (with an outbox row).</summary>
    Task ActivateAsync(HouseholdId household, CancellationToken cancellationToken);

    Task<LocalSummary> SummaryAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>
    /// Bring my entries over. The account's household becomes active. Its settings win; without them on the phone, the
    /// source's become its settings. A source that has ever synced is never moved: nothing moves and the result is false.
    /// </summary>
    Task<bool> BringOverAsync(BringOverPlan plan, UserId user, CancellationToken cancellationToken);

    /// <summary>Only with an empty outbox and no rejection: clears the household's records (not its settings) and its cursor. False otherwise.</summary>
    Task<bool> FreshBootstrapAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>After a confirmed removal (ADR 0002 §10): every record, outbox row, rejection and cursor of the household.</summary>
    Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary>Small sync facts in <c>app_state</c>.</summary>
public interface ISyncState
{
    Task<ConsentState> GetConsentAsync(UserId user, CancellationToken cancellationToken);

    Task SetConsentAsync(UserId user, ConsentState state, CancellationToken cancellationToken);

    Task<bool> IsSoftLockedAsync(HouseholdId household, CancellationToken cancellationToken);

    Task SetSoftLockAsync(HouseholdId household, bool locked, CancellationToken cancellationToken);
}

/// <summary>Connectivity regained (Spec 011 §6.3): a cycle at once, and the backoff resets.</summary>
public interface IConnectivityWatcher
{
    event EventHandler? Regained;

    void Start();
}

/// <summary>What the UI may ask of sync: a cycle soon, or one now that it can wait for.</summary>
public interface ISyncControl
{
    void RequestSync();

    Task SyncNowAsync(CancellationToken cancellationToken);
}

public enum SyncRefusal
{
    ConsentRequired,
    NotAMember,
    MembershipRevoked,
    ClientTooOld,
    Malformed,
}

/// <summary>The server answered and refused the request with one of its codes (Spec 011 §7.1).</summary>
public sealed class SyncRefusedException : Exception
{
    public SyncRefusedException()
    {
    }

    public SyncRefusedException(string message)
        : base(message)
    {
    }

    public SyncRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SyncRefusedException(SyncRefusal refusal, HouseholdId? household)
        : base(refusal.ToString())
    {
        Refusal = refusal;
        Household = household;
    }

    public SyncRefusal Refusal { get; }

    /// <summary>For <see cref="SyncRefusal.MembershipRevoked"/>: the household the problem body named.</summary>
    public HouseholdId? Household { get; }
}

/// <summary>Nothing was decided: no network, a timeout, 408, 429, 5xx, an unreadable body, any other refusal.</summary>
public sealed class SyncUnavailableException : Exception
{
    public SyncUnavailableException()
    {
    }

    public SyncUnavailableException(string message)
        : base(message)
    {
    }

    public SyncUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
