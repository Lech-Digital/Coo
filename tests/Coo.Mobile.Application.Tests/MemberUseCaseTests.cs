using Coo.Contracts.Households;
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
using NSubstitute.Extensions;
using static Coo.Mobile.Application.Tests.FamiliesTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §2 (the Family page) and §8 on the phone: nothing changes locally until the server said yes.</summary>
public sealed class MemberUseCaseTests : IDisposable
{
    private static readonly UserId Kasia = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c5"));
    private static readonly Guid InvitationId = Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1");
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IDeviceLocale _locale = Substitute.For<IDeviceLocale>();
    private readonly IInvitationCodes _codes = Substitute.For<IInvitationCodes>();
    private readonly List<Families> _made = [];

    public MemberUseCaseTests()
    {
        _account = new Account(_sessions);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined), Settings(Home)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 0, Phone));
        _locale.TimeZoneId.Returns("Europe/Warsaw");
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([new FamilyMember(Me, "Kasia", "k@example.com", Role.Owner, Now, true)], []));
        _sync.RunExclusiveAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<Func<CancellationToken, Task>>()(c.Arg<CancellationToken>()));
        _codes.ListAsync(default, default).ReturnsForAnyArgs(new Dictionary<Guid, InvitationCode>());
    }

    public void Dispose()
    {
        _account.Dispose();
        foreach (var families in _made)
            families.Dispose();
    }

    private Families Families()
    {
        var families = new Families(_account, _store, _accounts, _api, _sessions, _households, _moves, Substitute.For<ISyncState>(), _records,
            new ProvisionalSettings(_clock, _locale, new FakeTimeProvider(Now)));
        _made.Add(families);
        return families;
    }

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    private LeaveFamily Leave() => new(_api, Families(), _sync, _account);

    private RemoveFamilyMember Remove() => new(_api, Families(), _account);

    [Fact]
    public async Task ExecuteAsync_LoadOfflineAfterAFreshLoad_ReturnsTheLastListAsNotFresh()
    {
        // Arrange: spec §2, offline shows the last list fetched (ruling 24).
        var load = new LoadFamilyMembers(_api, _codes);
        var members = new FamilyMembers([new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now, true)], []);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(members);
        await load.ExecuteAsync(Joined, CancellationToken.None);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var again = await load.ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        again.Should().Be(new MembersLoad(members, IsFresh: false));
    }

    [Fact]
    public async Task ExecuteAsync_LoadFresh_ReturnsTheListAsFresh()
    {
        // Arrange
        var members = new FamilyMembers([new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now, true)], []);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(members);

        // Act
        var load = await new LoadFamilyMembers(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        load.Should().Be(new MembersLoad(members, IsFresh: true));
    }

    [Fact]
    public async Task ExecuteAsync_LoadRefusedAfterAFreshLoadOfAnotherFamily_ReturnsNothingNotFresh()
    {
        // Arrange: the last list is kept per family; a refusal (not a member any more) never shows another family's list.
        var load = new LoadFamilyMembers(_api, _codes);
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([], []));
        await load.ExecuteAsync(Home, CancellationToken.None);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.Forbidden));

        // Act
        var result = await load.ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        result.Should().Be(new MembersLoad(null, IsFresh: false));
    }

    [Fact]
    public async Task ExecuteAsync_InviteAnswered_ReturnsTheInvitation()
    {
        // Arrange
        var created = new CreatedInvitation(InvitationId, InvitationCode.Parse("ABCDEFGH").Value, new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), Now.AddHours(72));
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).Returns(created);

        // Act
        var result = await new InviteParent(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        result.Should().Be(new InviteResult(created, FamilyAction.Done));
    }

    [Fact]
    public async Task ExecuteAsync_InviteAnswered_RemembersTheCodeOnThisPhone()
    {
        // Arrange: walk fix F3, the phone that made an invitation shows its code.
        var code = InvitationCode.Parse("KAHNZ8FW").Value;
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).Returns(
            new CreatedInvitation(InvitationId, code, new Uri("https://api.coo.lechdigital.nl/i/KAHNZ8FW"), Now.AddHours(72)));

        // Act
        await new InviteParent(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        await _codes.Received(1).RememberAsync(Joined, InvitationId, code, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_InviteAnsweredButTheCodeCannotBeStored_StillReturnsTheInvitation()
    {
        // Arrange: the code on this phone is a convenience; the invitation exists and is shared anyway.
        var created = new CreatedInvitation(InvitationId, InvitationCode.Parse("KAHNZ8FW").Value, new Uri("https://api.coo.lechdigital.nl/i/KAHNZ8FW"), Now.AddHours(72));
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).Returns(created);
        _codes.RememberAsync(default, default, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("disk"));

        // Act
        var result = await new InviteParent(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        result.Should().Be(new InviteResult(created, FamilyAction.Done));
    }

    [Fact]
    public async Task ExecuteAsync_LoadFresh_ForgetsCodesOfInvitationsGoneAndReturnsTheOthers()
    {
        // Arrange
        var other = Guid.Parse("7a1c0000-0000-7000-8000-0000000000e2");
        var code = InvitationCode.Parse("KAHNZ8FW").Value;
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([], [
            new FamilyInvitation(InvitationId, Role.Parent, Me, Now, Now.AddHours(72)), new FamilyInvitation(other, Role.Parent, Me, Now, Now.AddHours(72))]));
        _codes.ListAsync(Joined, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, InvitationCode> { [InvitationId] = code });

        // Act
        var load = await new LoadFamilyMembers(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        await _codes.Received(1).KeepOnlyAsync(Joined, Arg.Is<IReadOnlyCollection<Guid>>(open => open.Count == 2 && open.Contains(InvitationId) && open.Contains(other)), Arg.Any<CancellationToken>());
        load.Codes.Should().Equal(new Dictionary<Guid, InvitationCode> { [InvitationId] = code });
    }

    [Fact]
    public async Task ExecuteAsync_LoadOffline_ForgetsNoCode()
    {
        // Arrange: the last list may be out of date; only a fresh list tells an invitation is gone.
        var code = InvitationCode.Parse("KAHNZ8FW").Value;
        var load = new LoadFamilyMembers(_api, _codes);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([], [new FamilyInvitation(InvitationId, Role.Parent, Me, Now, Now.AddHours(72))]));
        await load.ExecuteAsync(Joined, CancellationToken.None);
        _codes.ClearReceivedCalls();
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));
        _codes.ListAsync(Joined, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, InvitationCode> { [InvitationId] = code });

        // Act
        var again = await load.ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        await _codes.DidNotReceiveWithAnyArgs().KeepOnlyAsync(default, default!, default);
        again.Codes.Should().ContainKey(InvitationId);
    }

    [Fact]
    public async Task ExecuteAsync_LoadFreshButTheCodesCannotBeRead_StillReturnsTheList()
    {
        // Arrange
        var members = new FamilyMembers([], [new FamilyInvitation(InvitationId, Role.Parent, Me, Now, Now.AddHours(72))]);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(members);
        _codes.KeepOnlyAsync(default, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("disk"));

        // Act
        var load = await new LoadFamilyMembers(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        load.Should().Be(new MembersLoad(members, IsFresh: true));
    }

    [Fact]
    public async Task ExecuteAsync_InviteRefusedTooMany_ReportsTheCode()
    {
        // Arrange
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.InvitationTooMany));

        // Act
        var result = await new InviteParent(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        result.Invitation.Should().BeNull();
        result.Action.Should().Be(FamilyAction.Refused(HouseholdErrors.InvitationTooMany));
    }

    [Fact]
    public async Task ExecuteAsync_InviteOffline_IsUnavailable()
    {
        // Arrange
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var result = await new InviteParent(_api, _codes).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        result.Should().Be(new InviteResult(null, FamilyAction.Unavailable));
    }

    [Fact]
    public async Task ExecuteAsync_WithdrawAnswered_IsDone()
    {
        // Arrange
        // Act
        var result = await new WithdrawFamilyInvitation(_api).ExecuteAsync(Joined, InvitationId, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _api.Received(1).WithdrawInvitationAsync(Joined, InvitationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithdrawRefused_ReportsTheCode()
    {
        // Arrange
        _api.WithdrawInvitationAsync(Joined, InvitationId, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.Forbidden));

        // Act
        var result = await new WithdrawFamilyInvitation(_api).ExecuteAsync(Joined, InvitationId, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Refused(HouseholdErrors.Forbidden));
    }

    [Fact]
    public async Task ExecuteAsync_WithdrawOffline_IsUnavailable()
    {
        // Arrange
        _api.WithdrawInvitationAsync(Joined, InvitationId, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var result = await new WithdrawFamilyInvitation(_api).ExecuteAsync(Joined, InvitationId, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Unavailable);
    }

    [Fact]
    public async Task ExecuteAsync_RemoveAnswered_RefreshesTheFamilyList()
    {
        // Arrange
        await SignInAsync();

        // Act
        var result = await Remove().ExecuteAsync(Joined, Kasia, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _api.Received(1).RemoveMemberAsync(Joined, Kasia, Arg.Any<CancellationToken>());
        await _store.Received().ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l => l.Count == 1 && l[0].Household == Home), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RemoveAnswersMemberNotFound_IsAlreadyGoneSoItRefreshesAndSucceeds()
    {
        // Arrange: a retried remove (Task 2).
        await SignInAsync();
        _api.RemoveMemberAsync(Joined, Kasia, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.MemberNotFound));

        // Act
        var result = await Remove().ExecuteAsync(Joined, Kasia, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _store.Received().ReplaceAsync(Me, Arg.Any<IReadOnlyList<FamilyMembership>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RemoveRefusedForbidden_ReportsTheCodeAndRefreshesNothing()
    {
        // Arrange
        await SignInAsync();
        _api.RemoveMemberAsync(Joined, Kasia, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.Forbidden));

        // Act
        var result = await Remove().ExecuteAsync(Joined, Kasia, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Refused(HouseholdErrors.Forbidden));
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_RemoveOffline_IsUnavailableAndRefreshesNothing()
    {
        // Arrange
        await SignInAsync();
        _api.RemoveMemberAsync(Joined, Kasia, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var result = await Remove().ExecuteAsync(Joined, Kasia, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Unavailable);
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_RemoveAnsweredAfterTheAccountChanged_RefreshesNothing()
    {
        // Arrange: the answer belongs to the sign-in that asked (Increment 11 pattern).
        await SignInAsync();
        _api.RemoveMemberAsync(Joined, Kasia, Arg.Any<CancellationToken>()).Returns(_ => SignOutDuringAsync());

        // Act
        var result = await Remove().ExecuteAsync(Joined, Kasia, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _accounts.DidNotReceiveWithAnyArgs().GetMeAsync(default);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAnswered_RemovesTheFamilyFromThePhoneThenRefreshesAndTellsThePages()
    {
        // Arrange
        await SignInAsync();
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Left);

        // Act
        var result = await Leave().ExecuteAsync(Joined, confirm: true, CancellationToken.None);

        // Assert: the family is gone from the phone before the list refresh could drop it (a cycle never shows a removal sheet for one's own leave).
        result.Should().Be(FamilyAction.Done);
        Received.InOrder(() =>
        {
            _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>());
            _moves.DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>());
            _accounts.GetMeAsync(Arg.Any<CancellationToken>());
            _store.ReplaceAsync(Me, Arg.Any<IReadOnlyList<FamilyMembership>>(), Arg.Any<CancellationToken>());
        });
        _sync.Received(1).NotifyDataChanged();
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAnsweredWhileACycleRuns_TakesTheFamilyOffThePhoneOnlyInsideTheExclusiveSection()
    {
        // Arrange: final review C1. The engine holds the work until the cycle in flight has ended.
        await SignInAsync();
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Left);
        Func<CancellationToken, Task>? held = null;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sync.Configure().RunExclusiveAsync(default!, default).ReturnsForAnyArgs(async c =>
        {
            held = c.Arg<Func<CancellationToken, Task>>();
            await waiting.Task;
            await held(CancellationToken.None);
        });

        // Act
        var leave = Leave().ExecuteAsync(Joined, confirm: true, CancellationToken.None);
        var deletedBefore = _moves.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IHouseholdMoves.DeleteHouseholdAsync));
        var refreshedBefore = _store.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IFamilyStore.ReplaceAsync));
        waiting.SetResult();
        var result = await leave.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        held.Should().NotBeNull();
        deletedBefore.Should().BeFalse();
        refreshedBefore.Should().BeFalse();
        result.Should().Be(FamilyAction.Done);
        await _moves.Received(1).DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LeaveTheOnlyFamilyOnThePhone_DeletesAndReplacesItWithAProvisionalFamily()
    {
        // Arrange: spec §8.4.
        await SignInAsync();
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined)]);
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Deleted);

        // Act
        var result = await Leave().ExecuteAsync(Joined, confirm: true, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _records.Received(1).DeleteAndReplaceAsync(Joined, Arg.Is<HouseholdSettings>(s => s.Id != Joined), Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAnsweredAfterTheAccountChanged_DeletesNothingOnThePhone()
    {
        // Arrange
        await SignInAsync();
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(_ => SignOutDuringAsync(LeaveOutcome.Left));

        // Act
        var result = await Leave().ExecuteAsync(Joined, confirm: true, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
        _sync.DidNotReceive().NotifyDataChanged();
    }

    [Fact]
    public async Task ExecuteAsync_LeaveUnanswered_DeletesNothingOnThePhone()
    {
        // Arrange: Review Focus 4.
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var result = await Leave().ExecuteAsync(Joined, confirm: true, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Unavailable);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
        _sync.DidNotReceive().NotifyDataChanged();
    }

    [Fact]
    public async Task ExecuteAsync_LeaveCancelled_ThrowsAndTouchesNothingOnThePhone()
    {
        // Arrange
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        // Act
        Func<Task> act = async () => await Leave().ExecuteAsync(Joined, confirm: true, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
        _sync.DidNotReceive().NotifyDataChanged();
    }

    [Theory]
    [InlineData(HouseholdErrors.LastFamily)]
    [InlineData(HouseholdErrors.OwnerCannotLeave)]
    [InlineData(HouseholdErrors.ConfirmationRequired)]
    public async Task ExecuteAsync_LeaveRefused_ReportsTheCodeAndKeepsEverything(string code)
    {
        // Arrange
        _api.LeaveAsync(Joined, false, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(code));

        // Act
        var result = await Leave().ExecuteAsync(Joined, confirm: false, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Refused(code));
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
        _sync.DidNotReceive().RequestSync();
    }

    private async Task<LeaveOutcome> SignOutDuringAsync(LeaveOutcome outcome)
    {
        await SignOutDuringAsync();
        return outcome;
    }

    /// <summary>Signs out and the same person in again: the generation moves, yet a refresh would still find a session.</summary>
    private async Task SignOutDuringAsync()
    {
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await SignInAsync();
    }
}
