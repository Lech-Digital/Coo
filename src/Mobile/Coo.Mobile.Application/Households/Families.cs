using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>A family on this phone, as the Family section, the switcher and the bring-over see it (Spec 011b §7).</summary>
public sealed record FamilyOnPhone(
    HouseholdId Id, IReadOnlyList<string> BabyNames, FamilyMembership? Membership, bool IsShown, bool IsSoftLocked, bool IsHome, bool HasEverSynced)
{
    /// <summary>One of the signed-in account's families, on the server.</summary>
    public bool IsShared => Membership is not null;

    public bool IsOwner => Membership?.Role == Role.Owner;

    /// <summary>Invite, remove, withdraw (an Owner or a Parent).</summary>
    public bool CanManage => Membership?.Role is Role.Owner or Role.Parent;

    /// <summary>Spec 011b §6.3: a never-synced family moves; an account family that synced copies; another account's never.</summary>
    public bool MayBeBroughtOver => !HasEverSynced || IsShared;
}

/// <summary>What a confirmation or the removal sheet says about a family's log on this phone.</summary>
public sealed record FamilyContents(int EntryCount, IReadOnlyList<string> BabyNames, IReadOnlyList<StoredEntry> Unshared, VolumeUnit Unit);

/// <summary>
/// The account's families on this phone (Spec 011b §7): the list refreshed from <c>/api/me</c> and each family's members,
/// the shown family, switching, hiding a soft-locked family, and taking a family off the phone (§8.4: never the last one
/// without a new provisional family in its place). It raises nothing: callers tell the pages (<see cref="ISyncControl.NotifyDataChanged"/>).
/// </summary>
public sealed class Families(
    Account account, IFamilyStore store, IAccountApi accounts, IHouseholdApi api, ISessionStore sessions, IHouseholdStore households, IHouseholdMoves moves,
    ISyncState state, IFamilyRecords records, ProvisionalSettings provisional) : IDisposable
{
    private static readonly IComparer<Guid> ByGuid = Comparer<Guid>.Create(GuidOrder.Compare);

    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public void Dispose() => _refreshing.Dispose();

    /// <summary>
    /// Sign-out (ruling 27): the list goes, after any refresh in flight. That refresh began under the old sign-in, so it
    /// either stored before this or finds the generation moved and stores nothing (review M-c).
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            await store.ClearAsync(cancellationToken);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>False when it could not be refreshed (offline, signed out, the account changed meanwhile): the stored list stays.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        // Refreshes run one after another, so the one that started last stores last; one that began under an earlier
        // sign-in (the generation moved) stores nothing, even for the same person signing in again.
        var generation = account.Generation;
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            return await RefreshCoreAsync(generation, cancellationToken);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private async Task<bool> RefreshCoreAsync(long generation, CancellationToken cancellationToken)
    {
        if (generation != account.Generation || await sessions.GetAsync(cancellationToken) is not { } session)
            return false;
        AccountOverview me;
        try
        {
            me = await accounts.GetMeAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return false;
        }

        var user = session.Profile.UserId;
        if (me is null || me.Profile.UserId != user || me.Memberships is null)
            return false;

        var previous = await store.ListAsync(user, cancellationToken) ?? [];
        var list = new List<FamilyMembership>();
        foreach (var membership in me.Memberships.OrderBy(m => m.JoinedAt))
        {
            string owner;
            int count;
            try
            {
                var members = await api.ListMembersAsync(membership.Household, cancellationToken);
                owner = members.Members.FirstOrDefault(m => m.Role == Role.Owner)?.Name ?? "";
                count = Math.Max(1, members.Members.Count);
            }
            catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
            {
                var known = previous.FirstOrDefault(p => p.Household == membership.Household);
                (owner, count) = (known?.OwnerName ?? "", known?.MemberCount ?? 1);
            }

            list.Add(new FamilyMembership(membership.Household, membership.Role, membership.JoinedAt, owner, count));
        }

        if (generation != account.Generation || await sessions.GetAsync(cancellationToken) is not { } again || again.Profile.UserId != user)
            return false;
        await store.ReplaceAsync(user, list, cancellationToken);
        return true;
    }

    /// <summary>Every family on the phone: the account's by joining order, then the others by ID. <see cref="FamilyOnPhone.IsShown"/> marks the shown one.</summary>
    public async Task<IReadOnlyList<FamilyOnPhone>> ListAsync(CancellationToken cancellationToken)
    {
        var onPhone = (await households.ListHouseholdsAsync(cancellationToken)).Where(h => !h.IsDeleted).ToList();
        if (onPhone.Count == 0)
            return [];
        var session = await sessions.GetAsync(cancellationToken);
        var memberships = session is null ? [] : await store.ListAsync(session.Profile.UserId, cancellationToken) ?? [];
        var shown = await moves.ActiveHouseholdAsync(cancellationToken);
        var list = new List<FamilyOnPhone>(onPhone.Count);
        foreach (var household in onPhone)
        {
            var babies = (await households.ListBabiesAsync(household.Id, cancellationToken)).Where(b => !b.IsDeleted).Select(b => b.Name).ToList();
            list.Add(new FamilyOnPhone(
                household.Id, babies, memberships.FirstOrDefault(m => m.Household == household.Id), household.Id == shown,
                await state.IsSoftLockedAsync(household.Id, cancellationToken), session?.AccountHousehold == household.Id,
                await moves.HasEverSyncedAsync(household.Id, cancellationToken)));
        }

        return [.. list
            .OrderBy(f => f.Membership is null ? 1 : 0)
            .ThenBy(f => f.Membership?.JoinedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(f => f.Id.Value, ByGuid)];
    }

    public async Task<FamilyOnPhone?> ShownAsync(CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken)).FirstOrDefault(f => f.IsShown);

    /// <summary>The switcher (§7.3): only the shown family changes; nothing moves.</summary>
    public Task SwitchAsync(HouseholdId household, CancellationToken cancellationToken) => moves.ActivateAsync(household, cancellationToken);

    /// <summary>§8.2: a soft-locked shown family gives way to another (the earliest-joined account family, else any). True when it switched.</summary>
    public async Task<bool> HideSoftLockedAsync(CancellationToken cancellationToken)
    {
        var families = await ListAsync(cancellationToken);
        if (families.FirstOrDefault(f => f.IsShown) is not { IsSoftLocked: true } || families.FirstOrDefault(f => !f.IsSoftLocked) is not { } next)
            return false;
        await moves.ActivateAsync(next.Id, cancellationToken);
        return true;
    }

    /// <summary>Today's banner (§8.2): the shown family is soft-locked and is the only family on the phone.</summary>
    public async Task<bool> IsShownPausedAloneAsync(CancellationToken cancellationToken) =>
        await ListAsync(cancellationToken) is [{ IsSoftLocked: true }];

    /// <summary>
    /// After a confirmed removal, a leave or a delete (§8.2–§8.4): the family goes from the phone. The last one is replaced by
    /// a new provisional family in the same transaction; otherwise a shown one gives way to the earliest account family.
    /// </summary>
    public async Task RemoveFromPhoneAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        var others = (await households.ListHouseholdsAsync(cancellationToken)).Count(h => !h.IsDeleted && h.Id != household);
        if (others == 0)
        {
            await records.DeleteAndReplaceAsync(household, await provisional.CreateAsync(cancellationToken), cancellationToken);
            return;
        }

        var wasShown = await moves.ActiveHouseholdAsync(cancellationToken) == household;
        await moves.DeleteHouseholdAsync(household, cancellationToken);
        if (wasShown && (await ListAsync(cancellationToken)).FirstOrDefault(f => !f.IsSoftLocked) is { } next)
            await moves.ActivateAsync(next.Id, cancellationToken);
    }

    /// <summary>
    /// The families a cycle syncs: the account's families (ruling 6). Before the first refresh that is the home family; a home
    /// the known list lacks is no longer synced (the cycle moves the home, §7.1).
    /// </summary>
    public async Task<IReadOnlyCollection<HouseholdId>> SyncingHouseholdsAsync(CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return [];
        if (await store.ListAsync(session.Profile.UserId, cancellationToken) is not { } memberships)
            return [session.AccountHousehold];
        return memberships.Select(m => m.Household).ToHashSet();
    }

    /// <summary>Where a family's entries may go (§6.3 "Later"): another account family on the phone that is not soft-locked.</summary>
    public async Task<IReadOnlyList<FamilyOnPhone>> BringOverTargetsAsync(HouseholdId source, CancellationToken cancellationToken) =>
        [.. (await ListAsync(cancellationToken)).Where(f => f.Id != source && f.IsShared && !f.IsSoftLocked)];

    public async Task<FamilyContents> ContentsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        var summary = await moves.SummaryAsync(household, cancellationToken);
        var unit = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => h.Id == household)?.VolumeUnit ?? VolumeUnit.Millilitres;
        return new FamilyContents(
            summary.EntryCount, [.. summary.Babies.Select(b => b.Name)], await records.ListUnsharedEntriesAsync(household, cancellationToken), unit);
    }
}
