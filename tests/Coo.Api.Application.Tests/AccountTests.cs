using Coo.Api.Application.Auth;
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

    [Fact]
    public async Task ExecuteAsync_NotConfirmed_DeletesNothing()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(signedIn.User.Id, confirmed: false, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.ConfirmationRequired);
        _kit.Accounts.Deleted.Should().BeNull();
        _kit.Accounts.Households.Should().Contain(Household);
    }

    [Fact]
    public async Task ExecuteAsync_SoleOwner_DeletesTheUserAndTheHousehold()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(signedIn.User.Id, confirmed: true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Accounts.Deleted!.Value.User.Should().Be(signedIn.User.Id);
        _kit.Accounts.Deleted.Value.Plan.HouseholdsToDelete.Should().Equal(Household);
        _kit.Accounts.Deleted.Value.Plan.OwnershipTransfers.Should().BeEmpty();
        _kit.Accounts.BySubject.Should().BeEmpty();
        _kit.Accounts.Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_OwnerWithAnotherParent_PassesOwnershipAndKeepsTheHousehold()
    {
        // Arrange: from 11b a second parent can exist; the rule is in place already (ADR 0003 §8).
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);
        var partner = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
        _kit.Accounts.Memberships.Add(Membership.Create(Household, partner, Role.Parent, AuthKit.Start.AddDays(1)));

        // Act
        await _kit.DeleteAccount().ExecuteAsync(signedIn.User.Id, confirmed: true, CancellationToken.None);

        // Assert
        _kit.Accounts.Deleted!.Value.Plan.HouseholdsToDelete.Should().BeEmpty();
        _kit.Accounts.Deleted.Value.Plan.OwnershipTransfers.Should().Equal(new OwnershipTransfer(Household, partner));
    }
}
