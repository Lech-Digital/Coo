using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

/// <summary>
/// The four synced tables, <c>applied_changes</c> and <c>sync_conflicts</c> (Spec 011 §7.2). Each apply is one
/// transaction: the stored version under its row lock, <see cref="LastWriterWinsRule"/>, the household's next
/// <c>seq</c>, the row, the result, and a conflict row when something lost.
/// </summary>
public interface ISyncStore
{
    /// <summary>The result stored for a change ID, if it was decided before (a retried batch).</summary>
    Task<ChangeResult?> FindResultAsync(Guid changeId, CancellationToken cancellationToken);

    /// <summary>Applies a validated write. A concurrent decision for the same change ID wins; its result is returned.</summary>
    Task<ChangeResult> ApplyAsync(HouseholdId household, Guid changeId, SyncWrite write, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Stores a rejection for a change ID, unless a result is already stored; returns the stored result.</summary>
    Task<ChangeResult> RecordRejectionAsync(Guid changeId, string reason, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records of all four types with <c>seq &gt; cursor</c>, in <c>seq</c> order, at most <paramref name="limit"/>
    /// (ADR 0002 §6). From cursor 0 without a head, the head is read first; tombstones at or below the head are skipped,
    /// and the cursor still moves past them.
    /// </summary>
    Task<PullPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, int limit, CancellationToken cancellationToken);

    /// <summary>Deletes <c>applied_changes</c> and <c>sync_conflicts</c> rows older than <paramref name="before"/>.</summary>
    Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>A user's membership of a household: active, revoked (ADR 0002 §10), or never (<see cref="None"/>).</summary>
public sealed record MembershipLookup(Membership? Membership, bool IsRevoked)
{
    public static MembershipLookup None { get; } = new(null, false);
}

public interface IMembershipStore
{
    Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);
}

/// <summary>A consent as GDPR Art. 7(1) needs it proven: who, to which wording, when, from which app, in which language.</summary>
public sealed record ConsentRecord(UserId User, string Purpose, string Wording, DateTimeOffset GivenAt, string? AppVersion, string? Language);

/// <summary><c>consents</c>. Recording is idempotent: the first consent to a wording is kept.</summary>
public interface IConsentStore
{
    Task RecordAsync(ConsentRecord record, CancellationToken cancellationToken);

    Task<ConsentRecord?> FindAsync(UserId user, string purpose, string wording, CancellationToken cancellationToken);
}
