using Coo.Api.Application.Auth;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 010 §4.1 (<c>/api/me</c>) and §5 (deleting the account).</summary>
public sealed class AccountTests
{
    private static readonly DeviceId PhoneA = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DeviceId PhoneB = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d2"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));

    private readonly AuthKit _kit = new();

    [Fact]
    public async Task ExecuteAsync_TwoPhonesOneSignedOut_ShowsTheOwnerAndTheActivePhoneOnly()
    {
        // Arrange
        var a = await _kit.SignInAsync("google-1", PhoneA, Household);
        var b = await _kit.SignInAsync("google-1", PhoneB, Other);
        await _kit.SignOut().ExecuteAsync(a.Session.Id, CancellationToken.None);

        // Act
        var me = await _kit.GetMe().ExecuteAsync(a.User.Id, CancellationToken.None);

        // Assert
        me!.User.Email.Should().Be("google-1@example.com");
        me.Membership!.HouseholdId.Should().Be(Household);
        me.Membership.Role.Should().Be(Role.Owner);
        me.Devices.Should().ContainSingle().Which.Id.Should().Be(b.Session.Id);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownUser_ReturnsNull()
    {
        // Arrange
        var stranger = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9"));

        // Act
        var me = await _kit.GetMe().ExecuteAsync(stranger, CancellationToken.None);

        // Assert
        me.Should().BeNull();
    }

    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));

    [Fact]
    public async Task ExecuteAsync_NotConfirmed_DeletesNothing()
    {
        // Arrange
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: false, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.ConfirmationRequired);
        _kit.Households.DeletedUsers.Should().BeEmpty();
        _kit.Households.Households.Should().Contain(Household);
    }

    [Fact]
    public async Task ExecuteAsync_SoleOwner_DeletesTheUserAndTheHouseholdUnderItsLock()
    {
        // Arrange
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Households.LockedSets.Should().ContainSingle().Which.Should().Equal(Household);
        _kit.Households.Households.Should().NotContain(Household);
        _kit.Households.DeletedUsers.Should().Equal(Me);
        _kit.Households.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ParentJoinsBetweenTheReadAndTheLock_OwnershipPassesToTheJoiner()
    {
        // Arrange: the plan is decided from reads inside the transaction (spec §10.4): a concurrent accept that commits first counts.
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);
        _kit.Households.InsideNextTransaction = () => _kit.Households.AddMember(Household, Partner, Role.Parent, AuthKit.Start.AddDays(1));

        // Act
        await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        _kit.Households.Households.Should().Contain(Household);
        _kit.Households.Members.Should().ContainSingle().Which.Should().Match<FakeHouseholds.MemberRow>(m => m.User == Partner && m.Role == Role.Owner);
    }

    [Fact]
    public async Task ExecuteAsync_PromotionTouchesNoRow_ThrowsAndRollsBackEverything()
    {
        // Arrange: spec §10.4's row-count check; the phone's retry runs again from fresh reads.
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);
        _kit.Households.AddMember(Household, Partner, Role.Parent, AuthKit.Start.AddDays(1));
        _kit.Households.PromotionTouchesNothing = true;

        // Act
        Func<Task> act = async () => await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _kit.Households.DeletedUsers.Should().BeEmpty();
        _kit.Households.Members.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_FamilyGainedBetweenTheReadAndTheLock_ThrowsAndChangesNothing()
    {
        // Arrange: Me joins a second family after the families to lock were read; deciding it unlocked would race an accept.
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);
        _kit.Households.AddFamily(Other, Partner, AuthKit.Start);
        _kit.Households.InsideNextTransaction = () => _kit.Households.AddMember(Other, Me, Role.Parent, AuthKit.Start.AddDays(1));

        // Act
        Func<Task> act = async () => await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _kit.Households.DeletedUsers.Should().BeEmpty();
        _kit.Households.Households.Should().BeEquivalentTo([Household, Other]);
        _kit.Households.Members.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_ActiveInTwoFamilies_LocksBothAscendingAndDecidesEach()
    {
        // Arrange: Owner of Household alone, Parent in Other beside its Owner.
        _kit.Households.AddFamily(Other, Partner, AuthKit.Start);
        _kit.Households.AddMember(Other, Me, Role.Parent, AuthKit.Start.AddDays(1));
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);

        // Act
        await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        _kit.Households.LockedSets.Should().ContainSingle().Which.Should().Equal(Household, Other);
        _kit.Households.Households.Should().Equal(Other);
        _kit.Households.Members.Should().ContainSingle().Which.Should().Match<FakeHouseholds.MemberRow>(m => m.User == Partner && m.Role == Role.Owner);
    }

    [Fact]
    public async Task ExecuteAsync_RemovedFromAFamilyBefore_OnlyActiveFamiliesAreDecided()
    {
        // Arrange: a revoked row does not make Me "a parent" of that family.
        _kit.Households.AddFamily(Other, Partner, AuthKit.Start);
        _kit.Households.Members.Add(new FakeHouseholds.MemberRow(Other, Me, Role.Parent, AuthKit.Start, AuthKit.Start.AddDays(1)));
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);

        // Act
        await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        _kit.Households.Households.Should().Equal(Other);
        _kit.Households.Members.Should().ContainSingle(m => m.User == Partner && m.Role == Role.Owner);
    }

    [Fact]
    public async Task ExecuteAsync_MeWithTwoFamilies_ListsBothActiveMembershipsEarliestFirst()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);
        _kit.Accounts.Memberships.Insert(0, Membership.Create(Other, signedIn.User.Id, Role.Parent, AuthKit.Start.AddDays(2)));

        // Act
        var me = await _kit.GetMe().ExecuteAsync(signedIn.User.Id, CancellationToken.None);

        // Assert
        me!.Memberships!.Select(m => m.HouseholdId).Should().Equal(Household, Other);
        me.Membership!.HouseholdId.Should().Be(Household);
    }

    [Fact]
    public async Task ExecuteAsync_UserWithCurrentConsent_ReportsIt()
    {
        // Arrange
        var kit = new AuthKit();
        var signedIn = await kit.SignInAsync("sub-1", new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")),
            new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")));
        await kit.Consents.RecordAsync(new ConsentRecord(signedIn.User.Id, CareDataConsent.Purpose, CareDataConsent.CurrentWording,
            AuthKit.Start, "0.11.0", "en"), CancellationToken.None);

        // Act
        var me = await kit.GetMe().ExecuteAsync(signedIn.User.Id, CancellationToken.None);

        // Assert
        me!.Consent!.Wording.Should().Be(CareDataConsent.CurrentWording);
    }
}
