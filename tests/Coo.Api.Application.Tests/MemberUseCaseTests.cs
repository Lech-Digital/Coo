using Coo.Api.Application.Households;
using Coo.Contracts.Households;
using Coo.Domain.Households;
using static Coo.Api.Application.Tests.HouseholdKit;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 011b §8 and §10.1: the member list, removing, leaving, creating a family.</summary>
public sealed class MemberUseCaseTests
{
    private readonly HouseholdKit _kit = new();

    public MemberUseCaseTests()
    {
        _kit.Store.AddFamily(Family, Owner, Start.AddDays(-30), "Andrzej");
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-2), "Kasia");
    }

    [Fact]
    public async Task ExecuteAsync_ParentAsks_ListsActiveMembersAndOpenInvitations()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1), "Ola");
        await _kit.Remove().ExecuteAsync(Owner, Family, Nanny, CancellationToken.None);
        await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Act
        var view = await _kit.List().ExecuteAsync(Partner, Family, CancellationToken.None);

        // Assert
        view.Value.Members.Select(m => m.Name).Should().Equal("Andrzej", "Kasia");
        view.Value.Invitations.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverAsks_SeesMembersButNoInvitations()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1), "Ola");
        await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Act
        var view = await _kit.List().ExecuteAsync(Nanny, Family, CancellationToken.None);

        // Assert
        view.Value.Members.Should().HaveCount(3);
        view.Value.Invitations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RemovedMemberAsks_IsForbidden()
    {
        // Arrange
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var view = await _kit.List().ExecuteAsync(Partner, Family, CancellationToken.None);

        // Assert
        view.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_OwnerRemovesTheParent_RevokesAndWithdrawsTheirInvitations()
    {
        // Arrange
        await _kit.Create().ExecuteAsync(Partner, Family, "parent", CancellationToken.None);
        await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Act
        var result = await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Store.Members.Single(m => m.User == Partner).RevokedAt.Should().Be(Start, "the row stays for membership-revoked");
        _kit.Store.Invitations.Single(r => r.Invitation.CreatedBy == Partner).Invitation.RevokedAt.Should().Be(Start);
        _kit.Store.Invitations.Single(r => r.Invitation.CreatedBy == Owner).Invitation.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ParentRemovesTheOwner_IsForbidden()
    {
        // Act
        var result = await _kit.Remove().ExecuteAsync(Partner, Family, Owner, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        _kit.Store.Members.Single(m => m.User == Owner).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RemoveOneself_IsForbidden()
    {
        // Act
        var result = await _kit.Remove().ExecuteAsync(Partner, Family, Partner, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden, "leaving is its own call");
    }

    [Fact]
    public async Task ExecuteAsync_RemoveSomeoneNotInTheFamily_IsMemberNotFound()
    {
        // Act
        var result = await _kit.Remove().ExecuteAsync(Owner, Family, Nanny, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.MemberNotFound);
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverRemoves_IsForbiddenBeforeLookingUpTheTarget()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1), "Ola");

        // Act
        var result = await _kit.Remove().ExecuteAsync(Nanny, Family, Owner, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }
}
