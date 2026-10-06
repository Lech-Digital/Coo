using System.Collections.Concurrent;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>What a Family page action did: done, the server's code, or no answer (<see cref="Code"/> null).</summary>
public sealed record FamilyAction(bool Succeeded, string? Code = null)
{
    public static FamilyAction Done { get; } = new(true);

    public static FamilyAction Unavailable { get; } = new(false);

    public static FamilyAction Refused(string code) => new(false, code);

    internal static FamilyAction From(Exception e) => e is HouseholdRefusedException refused ? Refused(refused.Code) : Unavailable;
}

public sealed record MembersLoad(FamilyMembers? Members, bool IsFresh)
{
    internal static readonly IReadOnlyDictionary<Guid, InvitationCode> NoCodes = new Dictionary<Guid, InvitationCode>();

    /// <summary>The codes of the open invitations this phone made (walk fix F3), by invitation.</summary>
    public IReadOnlyDictionary<Guid, InvitationCode> Codes { get; init; } = NoCodes;
}

/// <summary>
/// The Family page's list (spec §2). Offline it returns the last list fetched in this app run (ruling 24). One per app. With it,
/// the codes of the open invitations this phone made; a fresh list forgets the codes of invitations no longer open.
/// </summary>
public sealed class LoadFamilyMembers(IHouseholdApi api, IInvitationCodes codes)
{
    private readonly ConcurrentDictionary<HouseholdId, FamilyMembers> _last = new();

    public async Task<MembersLoad> ExecuteAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        var rememberedBefore = codes.Remembered;
        MembersLoad load;
        try
        {
            var members = await api.ListMembersAsync(household, cancellationToken);
            _last[household] = members;
            load = new MembersLoad(members, IsFresh: true);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            load = new MembersLoad(_last.GetValueOrDefault(household), IsFresh: false);
        }

        // Only a fresh list asked for after the last code was remembered tells which codes are gone (review M8).
        var prune = load.IsFresh && codes.Remembered == rememberedBefore;
        return load.Members is { } list ? load with { Codes = await CodesAsync(household, list, prune, cancellationToken) } : load;
    }

    private async Task<IReadOnlyDictionary<Guid, InvitationCode>> CodesAsync(
        HouseholdId household, FamilyMembers members, bool fresh, CancellationToken cancellationToken)
    {
        try
        {
            var open = members.Invitations.Select(i => i.InvitationId).ToHashSet();
            if (fresh)
                await codes.KeepOnlyAsync(household, open, cancellationToken);
            var known = (await codes.ListAsync(household, cancellationToken)).Where(c => open.Contains(c.Key)).ToDictionary();
            return known.Count == 0 ? MembersLoad.NoCodes : known;
        }
#pragma warning disable CA1031 // The codes are a convenience of this phone: a failed local read or write never hides the list.
        catch (Exception)
#pragma warning restore CA1031
        {
            return MembersLoad.NoCodes;
        }
    }
}

public sealed record InviteResult(CreatedInvitation? Invitation, FamilyAction Action);

/// <summary>
/// Invite a parent (spec §3): the code exists only in this answer, on the share sheet, and on this phone's Family page (walk fix
/// F3), which remembers it by invitation.
/// </summary>
public sealed class InviteParent(IHouseholdApi api, IInvitationCodes codes)
{
    public async Task<InviteResult> ExecuteAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        CreatedInvitation created;
        try
        {
            created = await api.CreateInvitationAsync(household, cancellationToken);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return new InviteResult(null, FamilyAction.From(e));
        }

        try
        {
            await codes.RememberAsync(household, created.InvitationId, created.Code, cancellationToken);
        }
#pragma warning disable CA1031 // The invitation exists and is shared anyway; only this phone's reminder of its code is lost.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return new InviteResult(created, FamilyAction.Done);
    }
}

public sealed class WithdrawFamilyInvitation(IHouseholdApi api)
{
    public async Task<FamilyAction> ExecuteAsync(HouseholdId household, Guid invitationId, CancellationToken cancellationToken)
    {
        try
        {
            await api.WithdrawInvitationAsync(household, invitationId, cancellationToken);
            return FamilyAction.Done;
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return FamilyAction.From(e);
        }
    }
}

/// <summary>
/// Remove a member (spec §8.1), then refresh the family list (member counts). A retried remove answers
/// <c>household.member-not-found</c>: the member is already gone, which is what the person asked for.
/// </summary>
public sealed class RemoveFamilyMember(IHouseholdApi api, Families families, Account account)
{
    public async Task<FamilyAction> ExecuteAsync(HouseholdId household, UserId member, CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        try
        {
            await api.RemoveMemberAsync(household, member, cancellationToken);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.MemberNotFound)
        {
            // Already gone: fall through to the refresh.
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return FamilyAction.From(e);
        }

        // The answer belongs to the sign-in that asked; under another one the list is not ours to refresh.
        if (account.Generation == generation)
            await families.RefreshAsync(cancellationToken);
        return FamilyAction.Done;
    }
}

/// <summary>
/// Leave this family, or delete it (spec §8.3; also §6.2's Leave and Delete old family): only after the server's 200 is the
/// family taken off the phone (no soft-lock dance: the person just confirmed), and before the list refresh could drop it,
/// so the next cycle never shows a removal sheet for the person's own leave. A lost answer changes nothing here; the retry
/// answers 200 with the same outcome. Refusals (<see cref="HouseholdErrors.LastFamily"/>, <see cref="HouseholdErrors.OwnerCannotLeave"/>,
/// <see cref="HouseholdErrors.ConfirmationRequired"/>) come back as <see cref="FamilyAction.Refused"/> with the code.
/// </summary>
public sealed class LeaveFamily(IHouseholdApi api, Families families, ISyncControl sync, Account account)
{
    public async Task<FamilyAction> ExecuteAsync(HouseholdId household, bool confirm, CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        try
        {
            await api.LeaveAsync(household, confirm, cancellationToken);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return FamilyAction.From(e);
        }

        // Signed out or in again meanwhile: the server did leave, but this phone's family is not this sign-in's to delete.
        if (account.Generation != generation)
            return FamilyAction.Done;
        // Replace-versus-delete is decided from the phone's own family list, so the LeaveOutcome (left or deleted) is intentionally unused.
        // No cycle is in flight while the family goes and the list is refreshed: a pull answered before the leave must not
        // bring back its cursor, nor a stale list sync the family again (final review C1).
        var removed = false;
        await sync.RunExclusiveAsync(
            async ct =>
            {
                if (account.Generation != generation)
                    return;
                await families.RemoveFromPhoneAsync(household, ct);
                removed = true;
                await families.RefreshAsync(ct);
            },
            cancellationToken);
        if (!removed)
            return FamilyAction.Done;
        sync.NotifyDataChanged();
        sync.RequestSync();
        return FamilyAction.Done;
    }
}
