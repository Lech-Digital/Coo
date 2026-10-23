using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Domain.Tests.Households;

/// <summary>ADR 0003 §6 (what each role may do) and §8 (what happens to a household when a member leaves).</summary>
public sealed class MembershipTests
{
    private static readonly InvitationId InvitationKey = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"));
    private static readonly UserId Nanny = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c3"));
    private static readonly HouseholdId OtherHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Parent, true)]
    [InlineData(Role.Caregiver, false)]
    [InlineData(Role.Viewer, false)]
    public void Create_EachRole_GrantsTheRightsOfTheAdrTable(Role role, bool parentRights)
    {
        // Arrange
        var joinedAt = A.Now;

        // Act
        var membership = Membership.Create(A.Household, A.Parent, role, joinedAt);

        // Assert
        membership.IsParent.Should().Be(parentRights);
        membership.CanInvite.Should().Be(parentRights);
        membership.CanManageBaby.Should().Be(parentRights);
        membership.CanManageBilling.Should().Be(parentRights);
        membership.CanLog.Should().Be(role != Role.Viewer, "only a Viewer follows without logging");
    }

    [Fact]
    public void Create_UndefinedRole_Throws()
    {
        // Arrange
        var role = (Role)42;

        // Act
        var act = () => Membership.Create(A.Household, A.Parent, role, A.Now);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CanRemove_ParentRemovingCaregiver_IsAllowed()
    {
        // Arrange
        var parent = Membership.Create(A.Household, A.Parent, Role.Parent, A.Now);
        var nanny = Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now);

        // Act
        var allowed = parent.CanRemove(nanny);

        // Assert
        allowed.Should().BeTrue();
    }

    [Fact]
    public void CanRemove_ParentRemovingTheOwner_IsRefused()
    {
        // Arrange
        var owner = Membership.Create(A.Household, A.Parent, Role.Owner, A.Now);
        var parent = Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now);

        // Act
        var allowed = parent.CanRemove(owner);

        // Assert
        allowed.Should().BeFalse("the Owner cannot be removed (ADR 0003 §6)");
    }

    [Fact]
    public void CanRemove_CaregiverRemovingAnyone_IsRefused()
    {
        // Arrange
        var nanny = Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now);
        var parent = Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now);

        // Act
        var allowed = nanny.CanRemove(parent);

        // Assert
        allowed.Should().BeFalse();
    }

    [Fact]
    public void CanRemove_Themselves_IsRefused()
    {
        // Arrange: leaving is not removing; it is HouseholdSuccession's business.
        var parent = Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now);

        // Act
        var allowed = parent.CanRemove(parent);

        // Assert
        allowed.Should().BeFalse();
    }

    [Fact]
    public void CanRemove_MemberOfAnotherHousehold_IsRefused()
    {
        // Arrange
        var owner = Membership.Create(A.Household, A.Parent, Role.Owner, A.Now);
        var elsewhere = Membership.Create(OtherHousehold, Nanny, Role.Caregiver, A.Now);

        // Act
        var allowed = owner.CanRemove(elsewhere);

        // Assert
        allowed.Should().BeFalse();
    }

    [Fact]
    public void WhenLeaving_OnlyTheOwner_DeletesTheHousehold()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.Parent);

        // Assert
        succession.Should().BeOfType<HouseholdDeleted>();
    }

    [Fact]
    public void WhenLeaving_OwnerWithTwoParents_PassesOwnershipToTheEarliestInsteadOfDeleting()
    {
        // Arrange: the transfer rule runs before any deletion (ADR 0003, Consequences).
        var later = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c4"));
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, later, Role.Parent, A.Now.AddDays(9)),
            Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now.AddDays(3)),
        ];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.Parent);

        // Assert
        succession.Should().Be(new OwnershipPasses(A.OtherParent));
    }

    [Fact]
    public void WhenLeaving_OwnerWithOnlyACaregiver_DeletesTheHousehold()
    {
        // Arrange: the last parent leaves; a nanny alone does not keep the family's log.
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now.AddDays(1)),
        ];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.Parent);

        // Assert
        succession.Should().BeOfType<HouseholdDeleted>();
    }

    [Theory]
    [InlineData(Role.Parent)]
    [InlineData(Role.Caregiver)]
    public void WhenLeaving_NonOwnerWhileTheOwnerStays_LeavesTheHouseholdAsItIs(Role role)
    {
        // Arrange
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, A.OtherParent, role, A.Now.AddDays(1)),
        ];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.OtherParent);

        // Assert
        succession.Should().BeOfType<HouseholdRemains>();
    }

    [Fact]
    public void WhenLeaving_NotAMember_Throws()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var act = () => HouseholdSuccession.WhenLeaving(members, Nanny);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Parent, true)]
    [InlineData(Role.Caregiver, false)]
    public void CanWithdraw_EachRoleOfTheSameFamily_FollowsTheParentRight(Role role, bool expected)
    {
        // Arrange: any Owner or Parent may withdraw, not only the inviter (spec §15.2 Q3).
        var invitation = Invitation.Create(InvitationKey, A.Household, Role.Parent, A.OtherParent, A.Now);
        var member = Membership.Create(A.Household, A.Parent, role, A.Now);

        // Act
        var allowed = member.CanWithdraw(invitation);

        // Assert
        allowed.Should().Be(expected);
    }

    [Fact]
    public void CanWithdraw_InvitationOfAnotherFamily_IsRefused()
    {
        // Arrange
        var invitation = Invitation.Create(InvitationKey, OtherHousehold, Role.Parent, A.OtherParent, A.Now);
        var owner = Membership.Create(A.Household, A.Parent, Role.Owner, A.Now);

        // Act
        var allowed = owner.CanWithdraw(invitation);

        // Assert
        allowed.Should().BeFalse();
    }
}
