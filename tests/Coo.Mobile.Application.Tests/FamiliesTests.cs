using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §7: the account's families, the shown one, switching, hiding a soft-locked one, removal from the phone.</summary>
public sealed class FamiliesTests : IDisposable
{
    internal static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    internal static readonly AccountProfile Profile = new(Me, "Kasia", "kasia@example.com");
    internal static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    internal static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    internal static readonly HouseholdId Local = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3"));
    internal static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IDeviceLocale _locale = Substitute.For<IDeviceLocale>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Account _account;

    public FamiliesTests()
    {
        _account = new Account(_sessions);
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 0, Phone));
        _locale.TimeZoneId.Returns("Europe/Warsaw");
    }

    public void Dispose() => _account.Dispose();

    internal static HouseholdSettings Settings(HouseholdId id) =>
        HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;

    internal static BabyProfile Baby(HouseholdId household, string name) =>
        BabyProfile.Create(new BabyId(Guid.CreateVersion7(Now.AddMinutes(name.Length))), household, name, [], new DateOnly(2026, 8, 20), null,
            new HlcStamp(1_790_000_000_000, 0, Phone), new DateOnly(2026, 10, 6)).Value;

    private Families Families() => new(
        _account, _store, _accounts, _api, _sessions, _households, _moves, _state, _records, new ProvisionalSettings(_clock, _locale, _time));

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    /// <summary>Households on the phone, the first shown; each with the babies named.</summary>
    private void OnPhone(params (HouseholdId Id, string[] Babies)[] families)
    {
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([.. families.Select(f => Settings(f.Id))]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(families[0].Id);
        foreach (var (id, babies) in families)
            _households.ListBabiesAsync(id, Arg.Any<CancellationToken>()).Returns([.. babies.Select(b => Baby(id, b))]);
    }

    [Fact]
    public async Task RefreshAsync_TwoMemberships_StoresThemWithOwnerNamesAndCounts()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now.AddDays(1)), new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Me, "Kasia", "k@example.com", Role.Owner, Now, true)], []));
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9")), "Andrzej", "a@example.com", Role.Owner, Now, false),
             new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now.AddDays(1), true)], []));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeTrue();
        await _store.Received(1).ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l =>
            l.Count == 2 && l[0] == new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)
            && l[1] == new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_TwoMemberships_StoresEachFamilysMemberNamesForWhoLoggedIt()
    {
        // Arrange: walk fix F4, Today and Log name the parent who logged an entry.
        var andrzej = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9"));
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now.AddDays(1)), new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Me, "Kasia", "k@example.com", Role.Owner, Now, true)], []));
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(andrzej, "Andrzej Lech", "a@example.com", Role.Owner, Now, false),
             new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now.AddDays(1), true)], []));

        // Act
        await Families().RefreshAsync(CancellationToken.None);

        // Assert
        await _store.Received(1).SetMemberNamesAsync(Joined, Arg.Is<IReadOnlyList<FamilyMemberName>>(n =>
            n.SequenceEqual(new[] { new FamilyMemberName(andrzej, "Andrzej Lech", Role.Owner), new FamilyMemberName(Me, "Kasia", Role.Parent) })), Arg.Any<CancellationToken>());
        await _store.Received(1).SetMemberNamesAsync(Home, Arg.Is<IReadOnlyList<FamilyMemberName>>(n => n.Single() == new FamilyMemberName(Me, "Kasia", Role.Owner)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_SignedOutWhileTheListIsStored_WritesNoNames()
    {
        // Arrange: review M6, names of one account never outlive its sign-out.
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null, [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([new FamilyMember(Me, "Kasia", "k@example.com", Role.Owner, Now, true)], []));
        _store.ReplaceAsync(default, default!, default).ReturnsForAnyArgs(async _ => await _account.SignedOutAsync(false, CancellationToken.None));

        // Act
        await Families().RefreshAsync(CancellationToken.None);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SetMemberNamesAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_NamesAsStored_WritesNothingAndReportsNoChange()
    {
        // Arrange: review M7, the pages redraw only when a name changed.
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null, [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([new FamilyMember(Me, "Kasia", "k@example.com", Role.Owner, Now, true)], []));
        _store.ListMemberNamesAsync(Home, Arg.Any<CancellationToken>()).Returns([new FamilyMemberName(Me, "Kasia", Role.Owner)]);
        var families = Families();

        // Act
        await families.RefreshAsync(CancellationToken.None);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SetMemberNamesAsync(default, default!, default);
        families.TakeNamesChanged().Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_NamesStoredInAnotherOrder_WritesNothingAndReportsNoChange()
    {
        // Arrange: re-review N1, the server lists members by joining order and the store by user ID.
        await SignInAsync();
        var earlier = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f0"));
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null, [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Me, "Kasia", "k@example.com", Role.Owner, Now, true), new FamilyMember(earlier, "Tomek", "t@example.com", Role.Parent, Now, false)], []));
        _store.ListMemberNamesAsync(Home, Arg.Any<CancellationToken>()).Returns([new FamilyMemberName(earlier, "Tomek"), new FamilyMemberName(Me, "Kasia", Role.Owner)]);
        var families = Families();

        // Act
        await families.RefreshAsync(CancellationToken.None);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SetMemberNamesAsync(default, default!, default);
        families.TakeNamesChanged().Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ANameChanged_ReportsTheChangeOnce()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null, [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([new FamilyMember(Me, "Kasia Nowak", "k@example.com", Role.Owner, Now, true)], []));
        _store.ListMemberNamesAsync(Home, Arg.Any<CancellationToken>()).Returns([new FamilyMemberName(Me, "Kasia")]);
        var families = Families();

        // Act
        await families.RefreshAsync(CancellationToken.None);
        var first = families.TakeNamesChanged();
        var second = families.TakeNamesChanged();

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_MembersCallFails_KeepsTheStoredNames()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await Families().RefreshAsync(CancellationToken.None);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SetMemberNamesAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_MembersCallFails_KeepsTheLastKnownNameAndCount()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Joined, Role.Parent, Now, "Andrzej", 2)]);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await Families().RefreshAsync(CancellationToken.None);

        // Assert
        await _store.Received(1).ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l => l.Single().OwnerName == "Andrzej" && l.Single().MemberCount == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_Offline_ChangesNothing()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_AnswerForAnotherAccount_ChangesNothing()
    {
        // Arrange: the account changed while /api/me was in flight.
        await SignInAsync();
        var other = new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c8")), "Ola", "o@example.com");
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(other, Home, "owner", [], null, []));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task ListAsync_AccountAndLocalFamilies_OrdersAccountFamiliesByJoiningThenLocalOnes()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Local, ["Lena"]), (Joined, ["Lena"]), (Home, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var families = await Families().ListAsync(CancellationToken.None);

        // Assert
        families.Select(f => f.Id).Should().Equal(Home, Joined, Local);
        families[0].Should().Match<FamilyOnPhone>(f => f.IsHome && f.IsOwner && f.BabyNames.Count == 0 && !f.IsShown);
        families[1].Should().Match<FamilyOnPhone>(f => f.IsSoftLocked && f.CanManage && !f.IsOwner && f.BabyNames.Single() == "Lena");
        families[2].Should().Match<FamilyOnPhone>(f => f.IsShown && !f.IsShared);
    }

    [Fact]
    public async Task HideSoftLockedAsync_ShownIsSoftLockedAndAnotherExists_ShowsTheEarliestJoinedAccountFamily()
    {
        // Arrange: spec §8.2, ruling 13 of Spec 011.
        await SignInAsync();
        OnPhone((Joined, ["Lena"]), (Local, ["Leo"]), (Home, ["Lena"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var switched = await Families().HideSoftLockedAsync(CancellationToken.None);

        // Assert
        switched.Should().BeTrue();
        await _moves.Received(1).ActivateAsync(Home, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HideSoftLockedAsync_OnlyFamilyIsSoftLocked_StaysAndSaysPaused()
    {
        // Arrange: spec §15.2 Q6, logging carries on.
        await SignInAsync();
        OnPhone((Home, ["Lena"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Parent, Now, "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        var families = Families();

        // Act
        var switched = await families.HideSoftLockedAsync(CancellationToken.None);
        var paused = await families.IsShownPausedAloneAsync(CancellationToken.None);

        // Assert
        switched.Should().BeFalse();
        paused.Should().BeTrue();
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task ListAsync_TheEmptyFamilyLeftFromTheFirstStart_IsNotListed()
    {
        // Arrange: signed in from the welcome screen: the account's family is shown, the phone's empty first family stays.
        await SignInAsync();
        OnPhone((Home, ["Maja"]), (Local, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.SummaryAsync(Local, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));

        // Act
        var list = await Families().ListAsync(CancellationToken.None);

        // Assert
        list.Select(f => f.Id).Should().Equal(Home);
    }

    [Fact]
    public async Task ListAsync_APhoneOnlyFamilyWithEntriesButNoBaby_IsStillListed()
    {
        // Arrange: entries are never hidden, baby or not.
        await SignInAsync();
        OnPhone((Home, ["Maja"]), (Local, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.SummaryAsync(Local, Arg.Any<CancellationToken>()).Returns(new LocalSummary(4, []));

        // Act
        var list = await Families().ListAsync(CancellationToken.None);

        // Assert
        list.Select(f => f.Id).Should().Equal(Home, Local);
    }

    private SignOutOfPhone SignOutOfPhone(ISyncControl sync) => new(new SignOut(_accounts, _account), Families(), _records, sync);

    private static ISyncControl ExclusiveRunsAtOnce()
    {
        var sync = Substitute.For<ISyncControl>();
        sync.RunExclusiveAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        return sync;
    }

    [Fact]
    public async Task SignOutOfPhone_AnAccountFamilyAndThePhonesOwnLog_TakesOnlyTheAccountFamilyOff()
    {
        // Arrange: signed out as one account and in as another, the first one's family stayed on show (owner, 2026-11-03).
        await SignInAsync();
        OnPhone((Home, ["Maja"]), (Local, ["Maja"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 2)]);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Local, Arg.Any<CancellationToken>()).Returns(false);
        var sync = ExclusiveRunsAtOnce();

        // Act
        await SignOutOfPhone(sync).ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().BeOfType<SignedOut>();
        await _moves.Received(1).DeleteHouseholdAsync(Home, Arg.Any<CancellationToken>());
        await _moves.DidNotReceive().DeleteHouseholdAsync(Local, Arg.Any<CancellationToken>());
        sync.Received().NotifyDataChanged();
    }

    [Fact]
    public async Task SignOutOfPhone_OnlyAnAccountFamily_ReplacesItWithANewProvisionalFamily()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Home, ["Maja"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 2)]);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await SignOutOfPhone(ExclusiveRunsAtOnce()).ExecuteAsync(CancellationToken.None);

        // Assert: never no family at all; the welcome screen follows.
        await _records.Received(1).DeleteAndReplaceAsync(Home, Arg.Any<HouseholdSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignOutOfPhone_UnsentAfterPush_CountsOnlyTheLeavingFamilies()
    {
        // Arrange: three entries never reached the server; the phone's own log does not leave, so it is not counted.
        await SignInAsync();
        OnPhone((Home, ["Maja"]), (Local, ["Maja"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 2)]);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _records.CountPendingAsync(Arg.Is<IReadOnlyCollection<HouseholdId>>(h => h.Count == 1 && h.Contains(Home)), Arg.Any<CancellationToken>()).Returns(3);
        var sync = Substitute.For<ISyncControl>();
        sync.SyncNowAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        // Act
        var unsent = await SignOutOfPhone(sync).UnsentAfterPushAsync(CancellationToken.None);

        // Assert
        unsent.Should().Be(3);
        await sync.Received(1).SyncNowAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveFromPhoneAsync_TheLastFamily_DeletesAndReplacesItWithANewProvisionalFamilyInOneCall()
    {
        // Arrange: spec §8.4, ActiveHouseholdAsync must never throw after a removal.
        await SignInAsync();
        OnPhone((Joined, ["Lena"]));

        // Act
        await Families().RemoveFromPhoneAsync(Joined, CancellationToken.None);

        // Assert
        await _records.Received(1).DeleteAndReplaceAsync(Joined,
            Arg.Is<HouseholdSettings>(s => s.Id != Joined && s.TimeZoneId == "Europe/Warsaw"), Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task RemoveFromPhoneAsync_TheShownOneOfTwo_DeletesItAndShowsTheEarliestAccountFamily()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Joined, ["Lena"]), (Home, ["Leo"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.When(m => m.DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>())).Do(_ => OnPhone((Home, ["Leo"])));

        // Act
        await Families().RemoveFromPhoneAsync(Joined, CancellationToken.None);

        // Assert
        await _moves.Received(1).DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>());
        await _moves.Received(1).ActivateAsync(Home, Arg.Any<CancellationToken>());
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task SyncingHouseholdsAsync_HomeAndAccountFamilies_LeavesLocalOnesOut()
    {
        // Arrange: ruling 6, a local family's outbox never drains.
        await SignInAsync();
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([HomeMembership, JoinedMembership]);

        // Act
        var syncing = await Families().SyncingHouseholdsAsync(CancellationToken.None);

        // Assert
        syncing.Should().BeEquivalentTo([Home, Joined]);
    }

    [Fact]
    public async Task SyncingHouseholdsAsync_KnownListLacksTheHome_LeavesTheHomeOut()
    {
        // Arrange: the home family stopped being a membership; the cycle no longer syncs it (Task 14 moves the home).
        await SignInAsync();
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([JoinedMembership]);

        // Act
        var syncing = await Families().SyncingHouseholdsAsync(CancellationToken.None);

        // Assert
        syncing.Should().BeEquivalentTo([Joined]);
    }

    [Fact]
    public async Task BringOverTargetsAsync_SharedNotSoftLockedOthers_AreTheTargets()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Local, ["Lena"]), (Joined, ["Lena"]), (Home, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var targets = await Families().BringOverTargetsAsync(Local, CancellationToken.None);

        // Assert
        targets.Select(t => t.Id).Should().Equal(Joined);
    }

    private static readonly HlcStamp Later = new(1_790_000_000_001, 0, Phone);

    private static readonly FamilyMembership HomeMembership = new(Home, Role.Owner, Now, "Kasia", 1);
    private static readonly FamilyMembership JoinedMembership = new(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2);

    [Fact]
    public async Task RefreshAsync_SignedOut_ChangesNothingAndAsksNobody()
    {
        // Arrange

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _accounts.DidNotReceiveWithAnyArgs().GetMeAsync(default);
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_ServerRefusesTheCall_ChangesNothing()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthRejectedException("auth.refresh-invalid"));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_AnswerWithoutMemberships_ChangesNothing()
    {
        // Arrange: an older server does not list them; an empty list would wipe the stored one.
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", []));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_NoMembershipsAtAll_StoresAnEmptyList()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, null, null, [], null, []));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeTrue();
        await _store.Received(1).ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l => l.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_SignedOutWhileMembersAreRead_ChangesNothing()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _sessions.ClearAsync(CancellationToken.None);
            return new FamilyMembers([], []);
        });

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_AnotherAccountSignsInWhileMembersAreRead_ChangesNothing()
    {
        // Arrange
        await SignInAsync();
        var other = new StoredSession("a", Now.AddMinutes(15), "r", new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c8")), "Ola", "o@example.com"),
            Local, JoinPending: false);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _sessions.SaveAsync(other, CancellationToken.None);
            return new FamilyMembers([], []);
        });

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_FirstRefreshAndTheServerRefusesMembers_StoresAnEmptyNameAndOneMember()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<FamilyMembership>?)null);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException("household.not-a-member"));

        // Act
        await Families().RefreshAsync(CancellationToken.None);

        // Assert
        await _store.Received(1).ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l => l.Single() == new FamilyMembership(Joined, Role.Parent, Now, "", 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_MembersAnswerWithoutAnOwner_StoresAnEmptyNameAndAtLeastOneMember()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([], []));

        // Act
        await Families().RefreshAsync(CancellationToken.None);

        // Assert
        await _store.Received(1).ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l => l.Single() == new FamilyMembership(Joined, Role.Parent, Now, "", 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_NothingLiveOnThePhone_IsEmpty()
    {
        // Arrange
        await SignInAsync();
        var gone = Settings(Local);
        gone.Delete(Later);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([gone]);

        // Act
        var families = await Families().ListAsync(CancellationToken.None);

        // Assert
        families.Should().BeEmpty();
        await _moves.DidNotReceiveWithAnyArgs().ActiveHouseholdAsync(default);
    }

    [Fact]
    public async Task ListAsync_DeletedFamiliesAndBabies_AreLeftOut()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Local, ["Lena"]));
        var gone = Settings(Joined);
        gone.Delete(Later);
        var goneBaby = Baby(Local, "Leo");
        goneBaby.Delete(Later);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Local), gone]);
        _households.ListBabiesAsync(Local, Arg.Any<CancellationToken>()).Returns([Baby(Local, "Lena"), goneBaby]);

        // Act
        var families = await Families().ListAsync(CancellationToken.None);

        // Assert
        families.Single().Id.Should().Be(Local);
        families.Single().BabyNames.Should().Equal("Lena");
    }

    [Fact]
    public async Task ListAsync_SignedOut_ListsTheFamiliesAsLocalOnesWithNoHome()
    {
        // Arrange
        OnPhone((Local, ["Lena"]), (Home, []));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var families = await Families().ListAsync(CancellationToken.None);

        // Assert
        families.Select(f => f.Id).Should().Equal(Home, Local);
        families.Should().OnlyContain(f => !f.IsShared && !f.IsHome && !f.CanManage);
        families[0].HasEverSynced.Should().BeTrue();
        families[1].HasEverSynced.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ListAsync(default, default);
    }

    [Fact]
    public async Task ListAsync_BeforeTheFirstRefresh_TreatsEveryFamilyAsLocal()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Home, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<FamilyMembership>?)null);

        // Act
        var families = await Families().ListAsync(CancellationToken.None);

        // Assert
        families.Single().Should().Match<FamilyOnPhone>(f => !f.IsShared && f.IsHome);
    }

    [Theory]
    [InlineData(Role.Caregiver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Viewer)]
    public void MayBeBroughtOver_SomeoneElsesFamilyThatSynced_IsFalse(Role role)
    {
        // Arrange: a nanny's family is not hers to copy into another (owner's test, 2026-11-03).
        var family = new FamilyOnPhone(Joined, ["Maja"], new FamilyMembership(Joined, role, Now, "Andrzej", 2), IsShown: true, IsSoftLocked: false, IsHome: false, HasEverSynced: true);

        // Act
        var may = family.MayBeBroughtOver;

        // Assert
        may.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public void MayBeBroughtOver_NeverSyncedOrAnAccountFamily_IsTrue(bool hasEverSynced, bool shared, bool expected)
    {
        // Arrange
        var family = new FamilyOnPhone(Local, [], shared ? HomeMembership : null, IsShown: false, IsSoftLocked: false, IsHome: false, hasEverSynced);

        // Act
        var may = family.MayBeBroughtOver;

        // Assert
        may.Should().Be(expected);
    }

    [Fact]
    public void CanManage_OwnerAndParentOnly_NotACaregiverOrAnUnsharedFamily()
    {
        // Arrange
        FamilyOnPhone With(FamilyMembership? m) => new(Local, [], m, IsShown: false, IsSoftLocked: false, IsHome: false, HasEverSynced: false);

        // Act
        var results = new[]
        {
            With(HomeMembership).CanManage, With(JoinedMembership).CanManage, With(JoinedMembership with { Role = Role.Caregiver }).CanManage, With(null).CanManage,
        };

        // Assert
        results.Should().Equal(true, true, false, false);
    }

    [Fact]
    public async Task ShownAsync_ReturnsTheShownFamily_AndNullWithNoFamilies()
    {
        // Arrange
        OnPhone((Local, ["Lena"]), (Home, []));
        var families = Families();
        var shown = await families.ShownAsync(CancellationToken.None);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([]);

        // Act
        var none = await families.ShownAsync(CancellationToken.None);

        // Assert
        shown!.Id.Should().Be(Local);
        none.Should().BeNull();
    }

    [Fact]
    public async Task SwitchAsync_ActivatesOnlyTheFamilyNamed()
    {
        // Arrange

        // Act
        await Families().SwitchAsync(Joined, CancellationToken.None);

        // Assert
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task HideSoftLockedAsync_ShownIsNotSoftLocked_StaysWhereItIs()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Home, ["Lena"]), (Joined, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([HomeMembership, JoinedMembership]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var switched = await Families().HideSoftLockedAsync(CancellationToken.None);

        // Assert
        switched.Should().BeFalse();
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task HideSoftLockedAsync_EveryFamilyIsSoftLocked_StaysAndSaysNothingIsPausedAlone()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Joined, ["Lena"]), (Home, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([HomeMembership, JoinedMembership]);
        _state.IsSoftLockedAsync(Arg.Any<HouseholdId>(), Arg.Any<CancellationToken>()).Returns(true);
        var families = Families();

        // Act
        var switched = await families.HideSoftLockedAsync(CancellationToken.None);
        var paused = await families.IsShownPausedAloneAsync(CancellationToken.None);

        // Assert
        switched.Should().BeFalse();
        paused.Should().BeFalse();
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task IsShownPausedAloneAsync_NotSoftLocked_IsFalse()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Home, ["Lena"]));

        // Act
        var paused = await Families().IsShownPausedAloneAsync(CancellationToken.None);

        // Assert
        paused.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveFromPhoneAsync_NotTheShownOne_KeepsTheShownFamily()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Home, ["Leo"]), (Joined, ["Lena"]));

        // Act
        await Families().RemoveFromPhoneAsync(Joined, CancellationToken.None);

        // Assert
        await _moves.Received(1).DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task RemoveFromPhoneAsync_TheShownOneAndTheOthersAreSoftLocked_ActivatesNothing()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Joined, ["Lena"]), (Home, ["Leo"]));
        _moves.When(m => m.DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>())).Do(_ => OnPhone((Home, ["Leo"])));
        _state.IsSoftLockedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await Families().RemoveFromPhoneAsync(Joined, CancellationToken.None);

        // Assert
        await _moves.Received(1).DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task RemoveFromPhoneAsync_OtherFamiliesAreOnlyTombstones_ReplacesTheLastLiveOne()
    {
        // Arrange
        await SignInAsync();
        var gone = Settings(Local);
        gone.Delete(Later);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined), gone]);

        // Act
        await Families().RemoveFromPhoneAsync(Joined, CancellationToken.None);

        // Assert
        await _records.Received(1).DeleteAndReplaceAsync(Joined, Arg.Any<HouseholdSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncingHouseholdsAsync_SignedOut_IsEmpty()
    {
        // Arrange

        // Act
        var syncing = await Families().SyncingHouseholdsAsync(CancellationToken.None);

        // Assert
        syncing.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncingHouseholdsAsync_BeforeTheFirstRefresh_IsTheHomeFamily()
    {
        // Arrange
        await SignInAsync();
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<FamilyMembership>?)null);

        // Act
        var syncing = await Families().SyncingHouseholdsAsync(CancellationToken.None);

        // Assert
        syncing.Should().BeEquivalentTo([Home]);
    }

    [Fact]
    public async Task BringOverTargetsAsync_FromAnAccountFamily_LeavesOutItselfAndLocalFamilies()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Local, ["Lena"]), (Joined, ["Lena"]), (Home, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([HomeMembership, JoinedMembership]);

        // Act
        var targets = await Families().BringOverTargetsAsync(Joined, CancellationToken.None);

        // Assert
        targets.Select(t => t.Id).Should().Equal(Home);
    }

    [Fact]
    public async Task ContentsAsync_AFamilyInOunces_CarriesItsCountBabiesUnsharedEntriesAndUnit()
    {
        // Arrange
        var ounces = HouseholdSettings.Create(Local, VolumeUnit.Ounces, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home), ounces]);
        _moves.SummaryAsync(Local, Arg.Any<CancellationToken>()).Returns(new LocalSummary(7, [new LocalBaby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), "Lena")]));
        IReadOnlyList<StoredEntry> unshared = [];
        _records.ListUnsharedEntriesAsync(Local, Arg.Any<CancellationToken>()).Returns(unshared);

        // Act
        var contents = await Families().ContentsAsync(Local, CancellationToken.None);

        // Assert
        contents.Should().Match<FamilyContents>(c => c.EntryCount == 7 && c.BabyNames.Single() == "Lena" && c.Unit == VolumeUnit.Ounces);
        contents.Unshared.Should().BeSameAs(unshared);
    }

    [Fact]
    public async Task ContentsAsync_FamilyWithoutSettings_FallsBackToMillilitres()
    {
        // Arrange
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _moves.SummaryAsync(Local, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));

        // Act
        var contents = await Families().ContentsAsync(Local, CancellationToken.None);

        // Assert
        contents.Unit.Should().Be(VolumeUnit.Millilitres);
    }

    [Fact]
    public async Task CreateAsync_UnusableTimeZone_Throws()
    {
        // Arrange
        _locale.TimeZoneId.Returns("");
        var provisional = new ProvisionalSettings(_clock, _locale, _time);

        // Act
        Func<Task> act = async () => await provisional.CreateAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_DeviceInTheUs_PicksOuncesAndTheDevicesZone()
    {
        // Arrange
        _locale.PrefersOunces.Returns(true);
        _locale.TimeZoneId.Returns("America/New_York");

        // Act
        var settings = await new ProvisionalSettings(_clock, _locale, _time).CreateAsync(CancellationToken.None);

        // Assert
        settings.VolumeUnit.Should().Be(VolumeUnit.Ounces);
        settings.TimeZoneId.Should().Be("America/New_York");
    }

    private static readonly HouseholdId OtherLocal = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a4"));

    [Fact]
    public async Task ListAsync_TwoLocalFamilies_AreOrderedById()
    {
        // Arrange
        await SignInAsync();
        OnPhone((OtherLocal, []), (Local, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([HomeMembership]);

        // Act
        var families = await Families().ListAsync(CancellationToken.None);

        // Assert
        families.Select(f => f.Id).Should().Equal(Local, OtherLocal);
    }

    [Fact]
    public async Task RefreshAsync_OverlappingRefreshes_RunOneAfterAnotherSoTheLaterStartedListWins()
    {
        // Arrange
        await SignInAsync();
        var stored = new List<IReadOnlyList<FamilyMembership>>();
        _store.ReplaceAsync(Me, Arg.Do<IReadOnlyList<FamilyMembership>>(stored.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        var release = new TaskCompletionSource<FamilyMembers>();
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var families = Families();
        var first = families.RefreshAsync(CancellationToken.None);

        // Act
        var second = families.RefreshAsync(CancellationToken.None);
        var askedWhileFirstRuns = _accounts.ReceivedCalls().Count();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null, []));
        release.SetResult(new FamilyMembers([new FamilyMember(Me, "Old", "o@example.com", Role.Owner, Now, true)], []));
        await Task.WhenAll(first, second);

        // Assert
        askedWhileFirstRuns.Should().Be(1);
        stored.Should().HaveCount(2);
        stored[0].Should().ContainSingle(m => m.OwnerName == "Old");
        stored[1].Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_SameUserSignsOutAndInAgainWhileMembersAreRead_StoresNothingFromTheOldSession()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            await SignInAsync();
            return new FamilyMembers([], []);
        });

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_QueuedRefreshOutlivedBySignOutAndIn_AsksNobodyAndStoresNothing()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        var release = new TaskCompletionSource<FamilyMembers>();
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var families = Families();
        var first = families.RefreshAsync(CancellationToken.None);
        var queued = families.RefreshAsync(CancellationToken.None);

        // Act
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await SignInAsync();
        release.SetResult(new FamilyMembers([], []));
        await Task.WhenAll(first, queued);

        // Assert
        (await first).Should().BeFalse();
        (await queued).Should().BeFalse();
        await _accounts.Received(1).GetMeAsync(Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task ClearAsync_DuringARefresh_ClearsAfterTheRefreshStored()
    {
        // Arrange: review M-c, a refresh that passed its last check stores before the sign-out's clear, never after.
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([], []));
        var storing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.ReplaceAsync(Me, Arg.Any<IReadOnlyList<FamilyMembership>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            storing.TrySetResult();
            return release.Task;
        });
        using var families = Families();
        var refresh = families.RefreshAsync(CancellationToken.None);
        await storing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        var clear = families.ClearAsync(CancellationToken.None);
        var clearedWhileStoring = _store.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IFamilyStore.ClearAsync));
        release.SetResult();
        await refresh;
        await clear.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        clearedWhileStoring.Should().BeFalse();
        await _store.Received(1).ClearAsync(Arg.Any<CancellationToken>());
    }
}
