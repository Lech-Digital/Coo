using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Tests;

/// <summary>
/// <see cref="IHouseholdMembershipStore"/> in memory. A transaction works on copies and commits them only when the work
/// returns, so a throw inside it changes nothing, like the real one.
/// </summary>
internal sealed class FakeHouseholds : IHouseholdMembershipStore, IInvitationStore
{
    internal sealed record MemberRow(HouseholdId Household, UserId User, Role Role, DateTimeOffset JoinedAt, DateTimeOffset? RevokedAt);

    internal sealed record InvitationRow(Invitation Invitation, string Hash);

    public HashSet<HouseholdId> Households { get; } = [];

    public List<MemberRow> Members { get; } = [];

    public List<InvitationRow> Invitations { get; } = [];

    public Dictionary<UserId, (string Name, string Email)> Users { get; } = [];

    public Dictionary<HouseholdId, List<string>> Babies { get; } = [];

    /// <summary>Households holding any record (entries, babies, settings, dismissals), for the confirm rule.</summary>
    public HashSet<HouseholdId> WithRecords { get; } = [];

    public List<UserId> DeletedUsers { get; } = [];

    /// <summary>Runs inside the next transaction, after the locks: a concurrent request committing first.</summary>
    public Action? InsideNextTransaction { get; set; }

    /// <summary>Makes the next promotion touch no row (the real store's row count of 0).</summary>
    public bool PromotionTouchesNothing { get; set; }

    public List<IReadOnlyList<HouseholdId>> LockedSets { get; } = [];

    public void AddFamily(HouseholdId household, UserId owner, DateTimeOffset at, string name = "Andrzej")
    {
        Households.Add(household);
        Users.TryAdd(owner, (name, name.ToLowerInvariant() + "@example.com"));
        Members.Add(new MemberRow(household, owner, Role.Owner, at, null));
    }

    public void AddMember(HouseholdId household, UserId user, Role role, DateTimeOffset at, string name = "Kasia")
    {
        Users.TryAdd(user, (name, name.ToLowerInvariant() + "@example.com"));
        Members.Add(new MemberRow(household, user, role, at, null));
    }

    public async Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        LockedSets.Add([.. households.Order(Comparer<HouseholdId>.Create((a, b) => GuidOrder.Compare(a.Value, b.Value)))]);
        var hook = InsideNextTransaction;
        InsideNextTransaction = null;
        hook?.Invoke();
        var tx = new Transaction(this, [.. households.Where(Households.Contains)]);
        var result = await work(tx);
        tx.Commit();
        return result;
    }

    public Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MemberProfile>>([.. Members.Where(m => m.Household == household && m.RevokedAt is null)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new MemberProfile(m.User, Users[m.User].Name, Users[m.User].Email, m.Role, m.JoinedAt))]);

    public Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OpenInvitation>>([.. Invitations.Select(r => r.Invitation)
            .Where(i => i.HouseholdId == household && i.IsUsable(now))
            .OrderBy(i => i.CreatedAt)
            .Select(i => new OpenInvitation(i.Id, i.Role, i.CreatedBy, i.CreatedAt, i.ExpiresAt))]);

    public Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>([.. Members.Where(m => m.User == user && m.RevokedAt is null).OrderBy(m => m.JoinedAt).Select(ToMembership)]);

    public Task<FoundInvitation?> FindByHashAsync(string codeHash, CancellationToken cancellationToken)
    {
        var row = Invitations.FirstOrDefault(r => r.Hash == codeHash);
        return Task.FromResult(row is null ? null : new FoundInvitation(row.Invitation, Users[row.Invitation.CreatedBy].Name));
    }

    public Task<IReadOnlyList<string>> ListBabyNamesAsync(HouseholdId household, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Babies.TryGetValue(household, out var names) ? names : []);

    public Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(Invitations.RemoveAll(r => r.Invitation.ExpiresAt < now));

    internal static Membership ToMembership(MemberRow row) => Membership.Create(row.Household, row.User, row.Role, row.JoinedAt);

    private sealed class Transaction(FakeHouseholds store, HashSet<HouseholdId> locked) : IMembershipTransaction
    {
        private readonly List<MemberRow> _members = [.. store.Members];
        // Invitations are mutable Domain objects (Withdraw, Accept), so the copy holds clones: only Commit publishes a change.
        private readonly List<InvitationRow> _invitations = [.. store.Invitations.Select(r => r with { Invitation = Clone(r.Invitation) })];
        private readonly HashSet<HouseholdId> _households = [.. store.Households];
        private readonly List<UserId> _deletedUsers = [];

        private static Invitation Clone(Invitation i) =>
            Invitation.Rehydrate(i.Id, i.HouseholdId, i.Role, i.CreatedBy, i.CreatedAt, i.ExpiresAt, i.AcceptedBy, i.AcceptedAt, i.RevokedAt);

        public bool IsLocked(HouseholdId household) => locked.Contains(household);

        public Task<MembershipLookup> FindMembershipAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            var row = _members.FirstOrDefault(m => m.Household == household && m.User == user);
            return Task.FromResult(row is null ? MembershipLookup.None : new MembershipLookup(ToMembership(row), row.RevokedAt is not null));
        }

        public Task<IReadOnlyList<Membership>> ActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([.. _members.Where(m => m.Household == household && m.RevokedAt is null).Select(ToMembership)]);

        public Task<IReadOnlyList<Membership>> ActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([.. _members.Where(m => m.User == user && m.RevokedAt is null).Select(ToMembership)]);

        public Task<Invitation?> FindInvitationForUpdateAsync(string codeHash, CancellationToken cancellationToken) =>
            Task.FromResult(_invitations.FirstOrDefault(r => r.Hash == codeHash)?.Invitation);

        public Task<Invitation?> FindInvitationForUpdateAsync(HouseholdId household, InvitationId id, CancellationToken cancellationToken) =>
            Task.FromResult(_invitations.FirstOrDefault(r => r.Invitation.Id == id && r.Invitation.HouseholdId == household)?.Invitation);

        public Task<int> CountOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(_invitations.Count(r => r.Invitation.HouseholdId == household && r.Invitation.IsUsable(now)));

        public Task AddInvitationAsync(Invitation invitation, string codeHash, CancellationToken cancellationToken)
        {
            _invitations.Add(new InvitationRow(invitation, codeHash));
            return Task.CompletedTask;
        }

        // The transaction's cloned Domain object is the row: its state is already changed, and Commit publishes it.
        public Task SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken)
        {
            _members.Add(new MemberRow(membership.HouseholdId, membership.UserId, membership.Role, membership.JoinedAt, null));
            return Task.CompletedTask;
        }

        public Task ReactivateMembershipAsync(HouseholdId household, UserId user, Role role, DateTimeOffset joinedAt, CancellationToken cancellationToken)
        {
            var i = _members.FindIndex(m => m.Household == household && m.User == user);
            _members[i] = _members[i] with { Role = role, JoinedAt = joinedAt, RevokedAt = null };
            return Task.CompletedTask;
        }

        public Task RevokeMembershipAsync(HouseholdId household, UserId user, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var i = _members.FindIndex(m => m.Household == household && m.User == user && m.RevokedAt is null);
            if (i >= 0)
                _members[i] = _members[i] with { RevokedAt = now };
            return Task.CompletedTask;
        }

        public Task<int> WithdrawOpenInvitationsByAsync(HouseholdId household, UserId createdBy, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var open = _invitations.Where(r => r.Invitation.HouseholdId == household && r.Invitation.CreatedBy == createdBy && r.Invitation.IsOpen).ToList();
            foreach (var row in open)
                row.Invitation.Withdraw(now);
            return Task.FromResult(open.Count);
        }

        public Task<int> PromoteToOwnerAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            if (store.PromotionTouchesNothing)
                return Task.FromResult(0);
            var i = _members.FindIndex(m => m.Household == household && m.User == user && m.RevokedAt is null);
            if (i < 0)
                return Task.FromResult(0);
            _members[i] = _members[i] with { Role = Role.Owner };
            return Task.FromResult(1);
        }

        public Task<bool> HoldsAnyRecordAsync(HouseholdId household, CancellationToken cancellationToken) =>
            Task.FromResult(store.WithRecords.Contains(household));

        public Task<bool> TryCreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (!_households.Add(household))
                return Task.FromResult(false);
            _members.Add(new MemberRow(household, owner, Role.Owner, now, null));
            return Task.FromResult(true);
        }

        public Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
        {
            _households.Remove(household);
            _members.RemoveAll(m => m.Household == household);
            _invitations.RemoveAll(r => r.Invitation.HouseholdId == household);
            return Task.CompletedTask;
        }

        public Task DeleteMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken)
        {
            _members.RemoveAll(m => m.User == user);
            return Task.CompletedTask;
        }

        public Task DeleteUserAsync(UserId user, CancellationToken cancellationToken)
        {
            _members.RemoveAll(m => m.User == user);
            _invitations.RemoveAll(r => r.Invitation.CreatedBy == user);
            _deletedUsers.Add(user);
            return Task.CompletedTask;
        }

        public void Commit()
        {
            store.Members.Clear();
            store.Members.AddRange(_members);
            store.Invitations.Clear();
            store.Invitations.AddRange(_invitations);
            store.Households.Clear();
            store.Households.UnionWith(_households);
            store.DeletedUsers.AddRange(_deletedUsers);
        }
    }
}

/// <summary>The household use cases over the fakes.</summary>
internal sealed class HouseholdKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public static readonly HouseholdId Family = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    public static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    public static readonly UserId Owner = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    public static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
    public static readonly UserId Nanny = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c3"));

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } = new(Start);

    public FakeHouseholds Store { get; } = new();

    public FakeConsents Consents { get; } = new();

    private int _draws = -1;

    /// <summary>Codes are drawn from a counter, so each invitation of a test has its own: ABCDEFGH, then JKMNPQRS, …</summary>
    public CreateInvitation Create() => new(Store, Time, _ => Interlocked.Increment(ref _draws) % 31);

    public WithdrawInvitation Withdraw() => new(Store, Time);

    public ListMembers List() => new(Store, Time);

    public RemoveMember Remove() => new(Store, Time);

    public LeaveHousehold Leave() => new(Store, Time);

    public CreateHousehold CreateHousehold() => new(Store, Time);

    public PreviewInvitation Preview() => new(Store, new MembershipsOver(Store), Time);

    public AcceptInvitation Accept() => new(Store, Store, Consents, Time);

    public async Task GiveConsentAsync(UserId user) => await Consents.RecordAsync(
        new ConsentRecord(user, Contracts.Sync.CareDataConsent.Purpose, Contracts.Sync.CareDataConsent.CurrentWording, Start, "0.12.0", "en"),
        CancellationToken.None);

    /// <summary>The fake store's rows as <see cref="IMembershipStore"/> (the sync endpoints' per-request check) sees them.</summary>
    internal sealed class MembershipsOver(FakeHouseholds store) : FakeMemberships
    {
        public override Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            var row = store.Members.FirstOrDefault(m => m.Household == household && m.User == user);
            return Task.FromResult(row is null ? MembershipLookup.None : new MembershipLookup(FakeHouseholds.ToMembership(row), row.RevokedAt is not null));
        }
    }
}
