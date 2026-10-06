using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Households;

/// <summary>A member as the Family page shows them (spec §2): name and email come from their account.</summary>
public sealed record MemberProfile(UserId UserId, string Name, string Email, Role Role, DateTimeOffset JoinedAt);

public sealed record OpenInvitation(InvitationId Id, Role Role, UserId CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record FoundInvitation(Invitation Invitation, string InviterName);

/// <summary><c>invitations</c> outside a transaction: the preview's reads and the daily purge.</summary>
public interface IInvitationStore
{
    Task<FoundInvitation?> FindByHashAsync(string codeHash, CancellationToken cancellationToken);

    /// <summary>The live baby profiles' names, oldest first.</summary>
    Task<IReadOnlyList<string>> ListBabyNamesAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Every invitation whose expiry has passed, used and withdrawn ones included (spec §10.3).</summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Memberships and invitations. Every change runs in <see cref="RunLockedAsync"/> (spec §10.4).</summary>
public interface IHouseholdMembershipStore
{
    /// <summary>
    /// One transaction: the existing households among <paramref name="households"/> are locked (<c>FOR UPDATE</c>, in
    /// ascending ID order), then <paramref name="work"/> runs and the transaction commits. A throw rolls everything back.
    /// </summary>
    Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken);

    /// <summary>Active members, earliest joined first, with their names and emails.</summary>
    Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Neither used nor withdrawn, and not expired at <paramref name="now"/>; oldest first.</summary>
    Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Earliest joined first.</summary>
    Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);
}

/// <summary>Reads and writes inside <see cref="IHouseholdMembershipStore.RunLockedAsync"/>'s transaction.</summary>
public interface IMembershipTransaction
{
    /// <summary>Whether the household existed and is locked by this transaction.</summary>
    bool IsLocked(HouseholdId household);

    /// <summary>The row, revoked or not (ADR 0002 §10's distinction).</summary>
    Task<MembershipLookup> FindMembershipAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>The invitation with this code hash, locked.</summary>
    Task<Invitation?> FindInvitationForUpdateAsync(string codeHash, CancellationToken cancellationToken);

    Task<Invitation?> FindInvitationForUpdateAsync(HouseholdId household, InvitationId id, CancellationToken cancellationToken);

    Task<int> CountOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken);

    Task AddInvitationAsync(Invitation invitation, string codeHash, CancellationToken cancellationToken);

    /// <summary>Writes <c>accepted_by</c>, <c>accepted_at</c> and <c>revoked_at</c>.</summary>
    Task SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken);

    Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken);

    /// <summary>A revoked row comes back: <c>revoked_at = NULL</c>, the role, a new <c>joined_at</c>.</summary>
    Task ReactivateMembershipAsync(HouseholdId household, UserId user, Role role, DateTimeOffset joinedAt, CancellationToken cancellationToken);

    Task RevokeMembershipAsync(HouseholdId household, UserId user, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The user's open invitations for the household are withdrawn; returns how many.</summary>
    Task<int> WithdrawOpenInvitationsByAsync(HouseholdId household, UserId createdBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns the number of rows changed; the caller requires exactly one (spec §10.4).</summary>
    Task<int> PromoteToOwnerAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);

    /// <summary>Any care entry, baby profile, settings or dismissal, tombstones included.</summary>
    Task<bool> HoldsAnyRecordAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>The household and its Owner membership; false (and nothing written) when the ID is taken.</summary>
    Task<bool> TryCreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The household with all of its records, memberships and invitations.</summary>
    Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Every membership row of the user, revoked ones included.</summary>
    Task DeleteMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>The user with their logins, sessions, consents and invitations sent (ON DELETE CASCADE).</summary>
    Task DeleteUserAsync(UserId user, CancellationToken cancellationToken);
}
