using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>One active membership of the signed-in account (Spec 011b §7.1), with what the labels need.</summary>
public sealed record FamilyMembership(HouseholdId Household, Role Role, DateTimeOffset JoinedAt, string OwnerName, int MemberCount);

/// <summary>A code from a link or "Enter a code", kept in <c>app_state</c> with the time it arrived (spec §4.1).</summary>
public sealed record PendingInvitation(InvitationCode Code, DateTimeOffset ArrivedAt);

/// <summary>A member of a family as this phone last heard (walk fix F4): who logged an entry is named on Today and Log.</summary>
/// <param name="Role">For the nanny's handover: whose entries are a caregiver's day. Parent when not known (names stored before roles).</param>
public sealed record FamilyMemberName(UserId UserId, string Name, Coo.Domain.Households.Role Role = Coo.Domain.Households.Role.Parent);

/// <summary>Accepted, but the choice about this phone's entries is not made yet (spec §5.3).</summary>
public sealed record JoiningMarker(HouseholdId Joined, HouseholdId Source, string InviterName);

/// <summary><c>family_memberships</c> and the join's facts in <c>app_state</c>.</summary>
public interface IFamilyStore
{
    /// <summary>Earliest joined first; null until the list was first refreshed for this account.</summary>
    Task<IReadOnlyList<FamilyMembership>?> ListAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>In one transaction: the list is replaced and marked as this account's.</summary>
    Task ReplaceAsync(UserId user, IReadOnlyList<FamilyMembership> memberships, CancellationToken cancellationToken);

    /// <summary>Sign-out (spec §7.1, ruling 27): the list, the joining marker, member names and invitation codes go; a pending invitation stays.</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    Task<PendingInvitation?> GetPendingInvitationAsync(CancellationToken cancellationToken);

    Task SetPendingInvitationAsync(PendingInvitation? invitation, CancellationToken cancellationToken);

    Task<JoiningMarker?> GetJoiningAsync(CancellationToken cancellationToken);

    Task SetJoiningAsync(JoiningMarker? marker, CancellationToken cancellationToken);

    /// <summary>Spec 011b §6.2's <c>copied_from</c>: the bring-over is not offered again for this pair.</summary>
    Task<bool> WasCopiedAsync(HouseholdId source, HouseholdId target, CancellationToken cancellationToken);

    /// <summary>The family's members as the last list refresh found them (walk fix F4); empty when not known. Cleared at sign-out.</summary>
    Task<IReadOnlyList<FamilyMemberName>> ListMemberNamesAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Replaces the family's member names.</summary>
    Task SetMemberNamesAsync(HouseholdId household, IReadOnlyList<FamilyMemberName> names, CancellationToken cancellationToken);
}

/// <summary>
/// The codes of the invitations this phone made (walk fix F3), by family and invitation: the server keeps only a hash, so only the
/// phone that made one can show its code. Plain app storage: once shared a code is not a secret, but it is never logged.
/// </summary>
public interface IInvitationCodes
{
    /// <summary>How many codes were remembered in this app run: a list asked for before one was made must not forget it (review M8).</summary>
    long Remembered { get; }

    Task<IReadOnlyDictionary<Guid, InvitationCode>> ListAsync(HouseholdId household, CancellationToken cancellationToken);

    Task RememberAsync(HouseholdId household, Guid invitationId, InvitationCode code, CancellationToken cancellationToken);

    /// <summary>Forgets the family's codes whose invitations are not in <paramref name="open"/>.</summary>
    Task KeepOnlyAsync(HouseholdId household, IReadOnlyCollection<Guid> open, CancellationToken cancellationToken);
}

public sealed record CopyResult(int Copied, int Skipped);

/// <summary>Whole-family operations on this phone, each in one SQLite transaction.</summary>
public interface IFamilyRecords
{
    /// <summary>Care entries, baby profiles and dismissals of the household, tombstones included: the copy's stamp budget.</summary>
    Task<int> CountRecordsAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 011b §6.2: the source's readable live records become new records of the target, stamped from
    /// <paramref name="firstStamp"/> with consecutive counters; the source is untouched; <c>copied_from</c> is written in
    /// the same transaction and the target becomes active. Null, with nothing written, when the pair was copied before.
    /// <paramref name="reservedCount"/> is how many stamps the caller reserved on the clock: a copy that would use more throws
    /// <see cref="StampBudgetExceededException"/> and writes nothing.
    /// </summary>
    Task<CopyResult?> CopyAsync(BringOverPlan plan, UserId user, HlcStamp firstStamp, int reservedCount, CancellationToken cancellationToken);

    /// <summary>Spec 011b §8.4: the household goes and <paramref name="replacement"/> (with an outbox row) becomes the active one.</summary>
    Task DeleteAndReplaceAsync(HouseholdId household, HouseholdSettings replacement, CancellationToken cancellationToken);

    /// <summary>Distinct records waiting in the outbox across these households (ruling 6).</summary>
    Task<int> CountPendingAsync(IReadOnlyCollection<HouseholdId> households, CancellationToken cancellationToken);

    /// <summary>The household's live care entries that wait in the outbox, oldest first (the removal sheet, spec §8.2).</summary>
    Task<IReadOnlyList<StoredEntry>> ListUnsharedEntriesAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary>A copy needed more stamps than its caller reserved on the clock (ruling 8): nothing was written; count and reserve again.</summary>
public sealed class StampBudgetExceededException : Exception
{
    public StampBudgetExceededException()
        : base("The copy needs more stamps than were reserved for it.")
    {
    }

    public StampBudgetExceededException(string message)
        : base(message)
    {
    }

    public StampBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record CreatedInvitation(Guid InvitationId, InvitationCode Code, Uri Link, DateTimeOffset ExpiresAt);

public sealed record FamilyMember(UserId UserId, string Name, string Email, Role Role, DateTimeOffset JoinedAt, bool IsYou);

public sealed record FamilyInvitation(Guid InvitationId, Role Role, UserId CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record FamilyMembers(IReadOnlyList<FamilyMember> Members, IReadOnlyList<FamilyInvitation> Invitations);

public sealed record InvitationPreview(
    HouseholdId Household, string InviterName, IReadOnlyList<string> BabyNames, Role Role, DateTimeOffset ExpiresAt, bool AlreadyMember);

public enum AcceptOutcome
{
    Joined,
    Rejoined,
    AlreadyMember,
}

public sealed record AcceptedInvitation(HouseholdId Household, Role Role, AcceptOutcome Outcome);

public enum LeaveOutcome
{
    Left,
    Deleted,
}

/// <summary>
/// <c>/api/households</c> and <c>/api/invitations</c> through the refreshing handler (Spec 011b §10.1). Throws
/// <see cref="HouseholdUnavailableException"/> when nothing was decided and <see cref="HouseholdRefusedException"/> for a
/// code the server sent.
/// </summary>
public interface IHouseholdApi
{
    Task<CreatedInvitation> CreateInvitationAsync(HouseholdId household, Role role, CancellationToken cancellationToken);

    Task WithdrawInvitationAsync(HouseholdId household, Guid invitationId, CancellationToken cancellationToken);

    Task<FamilyMembers> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    Task RemoveMemberAsync(HouseholdId household, UserId member, CancellationToken cancellationToken);

    Task<LeaveOutcome> LeaveAsync(HouseholdId household, bool confirm, CancellationToken cancellationToken);

    Task CreateHouseholdAsync(HouseholdId household, CancellationToken cancellationToken);

    Task<InvitationPreview> PreviewAsync(InvitationCode code, CancellationToken cancellationToken);

    Task<AcceptedInvitation> AcceptAsync(InvitationCode code, CancellationToken cancellationToken);
}

/// <summary>Nothing was decided: no network, a timeout, 5xx, an unreadable or foreign answer (a portal, a bare 403).</summary>
public sealed class HouseholdUnavailableException : Exception
{
    public HouseholdUnavailableException()
    {
    }

    public HouseholdUnavailableException(string message)
        : base(message)
    {
    }

    public HouseholdUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The server refused with one of its codes (<c>HouseholdErrors</c>, <c>auth.rate-limited</c>, <c>sync.consent-required</c>).</summary>
public sealed class HouseholdRefusedException : Exception
{
    public HouseholdRefusedException()
        : this("unknown")
    {
    }

    public HouseholdRefusedException(string message)
        : base(message)
    {
    }

    public HouseholdRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code => Message;
}
