using Coo.Api.Application.Households;
using Coo.Contracts.Households;
using Coo.Domain.Common;
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
        _kit.Store.LockedSets.Last().Should().Equal(Family);
    }

    [Fact]
    public async Task ExecuteAsync_ParentRemovesAnotherParent_RevokesThem()
    {
        // Arrange
        var second = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c4"));
        _kit.Store.AddMember(Family, second, Role.Parent, Start.AddDays(-1), "Ewa");

        // Act
        var result = await _kit.Remove().ExecuteAsync(Partner, Family, second, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Store.Members.Single(m => m.User == second).RevokedAt.Should().Be(Start);
    }

    [Fact]
    public async Task ExecuteAsync_RemovedMemberRemoves_IsForbidden()
    {
        // Arrange: the target is removable (not the Owner), so only the remover's own revocation can refuse.
        _kit.Store.AddMember(Family, Nanny, Role.Parent, Start.AddDays(-1), "Ola");
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var result = await _kit.Remove().ExecuteAsync(Partner, Family, Nanny, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        _kit.Store.Members.Single(m => m.User == Nanny).RevokedAt.Should().BeNull();
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
        var nobody = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9"));

        // Act
        var result = await _kit.Remove().ExecuteAsync(Nanny, Family, nobody, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_ParentWithAnotherFamilyLeaves_EndsTheMembershipAndWithdrawsTheirInvitations()
    {
        // Arrange
        _kit.Store.AddFamily(Other, Partner, Start.AddDays(-60), "Kasia");
        await _kit.Create().ExecuteAsync(Partner, Family, "parent", CancellationToken.None);

        // Act
        var result = await _kit.Leave().ExecuteAsync(Partner, Family, confirm: false, CancellationToken.None);

        // Assert
        result.Value.Should().Be(LeaveOutcome.Left);
        _kit.Store.Members.Single(m => m.User == Partner && m.Household == Family).RevokedAt.Should().Be(Start);
        _kit.Store.Invitations.Single().Invitation.RevokedAt.Should().Be(Start);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveRetriedAfterLeaving_AnswersLeftAgain()
    {
        // Arrange: Review Focus 4, the first answer was lost.
        _kit.Store.AddFamily(Other, Partner, Start.AddDays(-60), "Kasia");
        await _kit.Leave().ExecuteAsync(Partner, Family, confirm: false, CancellationToken.None);

        // Act
        var again = await _kit.Leave().ExecuteAsync(Partner, Family, confirm: false, CancellationToken.None);

        // Assert
        again.Value.Should().Be(LeaveOutcome.Left);
    }

    [Fact]
    public async Task ExecuteAsync_ParentsOnlyFamily_IsLastFamily()
    {
        // Act
        var result = await _kit.Leave().ExecuteAsync(Partner, Family, confirm: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.LastFamily);
        _kit.Store.Members.Single(m => m.User == Partner).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OwnerWithMembersLeaves_IsOwnerCannotLeave()
    {
        // Arrange
        _kit.Store.AddFamily(Other, Owner, Start.AddDays(-90));

        // Act
        var result = await _kit.Leave().ExecuteAsync(Owner, Family, confirm: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.OwnerCannotLeave);
    }

    [Fact]
    public async Task ExecuteAsync_OwnerAloneWithoutConfirmOnAnEmptyFamily_DeletesIt()
    {
        // Arrange: §6.1's automatic leave of an emptied adopted family.
        _kit.Store.AddFamily(Other, Nanny, Start.AddDays(-1), "Ola");
        _kit.Store.AddFamily(new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3")), Nanny, Start.AddDays(-9), "Ola");

        // Act
        var result = await _kit.Leave().ExecuteAsync(Nanny, Other, confirm: false, CancellationToken.None);

        // Assert
        result.Value.Should().Be(LeaveOutcome.Deleted);
        _kit.Store.Households.Should().NotContain(Other);
    }

    [Fact]
    public async Task ExecuteAsync_OwnerAloneWithoutConfirmOnAFamilyWithRecords_IsConfirmationRequired()
    {
        // Arrange: another phone of hers pushed into it (spec §6.1).
        _kit.Store.AddFamily(Other, Nanny, Start.AddDays(-1), "Ola");
        _kit.Store.AddFamily(new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3")), Nanny, Start.AddDays(-9), "Ola");
        _kit.Store.WithRecords.Add(Other);

        // Act
        var unconfirmed = await _kit.Leave().ExecuteAsync(Nanny, Other, confirm: false, CancellationToken.None);
        var confirmed = await _kit.Leave().ExecuteAsync(Nanny, Other, confirm: true, CancellationToken.None);

        // Assert
        unconfirmed.Error!.Code.Should().Be(HouseholdErrors.ConfirmationRequired);
        confirmed.Value.Should().Be(LeaveOutcome.Deleted);
        _kit.Store.Households.Should().NotContain(Other);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAFamilyThatIsGone_AnswersDeleted()
    {
        // Act: a retried delete whose first answer was lost (ruling 15).
        var result = await _kit.Leave().ExecuteAsync(Partner, Other, confirm: true, CancellationToken.None);

        // Assert
        result.Value.Should().Be(LeaveOutcome.Deleted);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAFamilyNeverJoined_IsForbidden()
    {
        // Arrange
        _kit.Store.AddFamily(Other, Nanny, Start.AddDays(-1), "Ola");

        // Act
        var result = await _kit.Leave().ExecuteAsync(Partner, Other, confirm: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_CreateForAUserWithNoActiveFamily_CreatesItWithThemAsOwner()
    {
        // Arrange: §8.4, removed from their only family.
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var result = await _kit.CreateHousehold().ExecuteAsync(Partner, Other, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Store.Members.Should().ContainSingle(m => m.Household == Other && m.User == Partner && m.Role == Role.Owner);
    }

    [Fact]
    public async Task ExecuteAsync_CreateWithATakenId_IsIdTaken()
    {
        // Arrange
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var result = await _kit.CreateHousehold().ExecuteAsync(Partner, Family, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.IdTaken);
    }

    [Fact]
    public async Task ExecuteAsync_CreateWhileInAFamily_IsAlreadyHasFamily()
    {
        // Act
        var result = await _kit.CreateHousehold().ExecuteAsync(Partner, Other, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.AlreadyHasFamily);
        _kit.Store.Households.Should().NotContain(Other);
    }

    [Fact]
    public async Task ExecuteAsync_CreateRetriedAfterALostAnswer_Succeeds()
    {
        // Arrange: §8.4, the first answer was lost; the family exists with them as its only Owner.
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);
        await _kit.CreateHousehold().ExecuteAsync(Partner, Other, CancellationToken.None);

        // Act
        var again = await _kit.CreateHousehold().ExecuteAsync(Partner, Other, CancellationToken.None);

        // Assert
        again.IsSuccess.Should().BeTrue();
        _kit.Store.Members.Count(m => m.Household == Other).Should().Be(1);
    }
}
