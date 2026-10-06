using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Domain.Tests.Households;

/// <summary>Spec 011b §8.3: each row of the leaving table.</summary>
public sealed class HouseholdLeavingTests
{
    private static readonly UserId Nanny = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c3"));

    [Theory]
    [InlineData(Role.Parent)]
    [InlineData(Role.Caregiver)]
    public void Decide_NonOwnerWithAnotherFamily_EndsTheMembership(Role role)
    {
        // Arrange
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, A.OtherParent, role, A.Now.AddDays(1)),
        ];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.OtherParent, hasOtherFamily: true);

        // Assert
        decision.Should().Be(LeaveDecision.MembershipEnds);
    }

    [Fact]
    public void Decide_OwnerAloneWithAnotherFamily_DeletesTheFamily()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: true);

        // Assert
        decision.Should().Be(LeaveDecision.HouseholdDeleted);
    }

    [Fact]
    public void Decide_OwnerWithACaregiver_IsRefused()
    {
        // Arrange: ownership transfer is deferred (ADR 0003 §6).
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now.AddDays(1)),
        ];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: true);

        // Assert
        decision.Should().Be(LeaveDecision.RefusedOwnerWithMembers);
    }

    [Fact]
    public void Decide_OwnerWithMembersAndNoOtherFamily_IsRefusedAsOwnerFirst()
    {
        // Arrange: ruling 15, the more specific refusal wins.
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now.AddDays(1)),
        ];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: false);

        // Assert
        decision.Should().Be(LeaveDecision.RefusedOwnerWithMembers);
    }

    [Theory]
    [InlineData(Role.Owner)]
    [InlineData(Role.Parent)]
    public void Decide_OnlyFamily_IsRefused(Role role)
    {
        // Arrange
        Membership[] members = role == Role.Owner
            ? [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)]
            : [Membership.Create(A.Household, A.OtherParent, Role.Owner, A.Now), Membership.Create(A.Household, A.Parent, Role.Parent, A.Now.AddDays(1))];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: false);

        // Assert
        decision.Should().Be(LeaveDecision.RefusedLastFamily);
    }

    [Fact]
    public void Decide_NotAMember_Throws()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var act = () => HouseholdLeaving.Decide(members, Nanny, hasOtherFamily: true);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
