using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>What leaving a family does (Spec 011b §8.3).</summary>
public enum LeaveDecision
{
    /// <summary>A Parent or Caregiver: their membership ends.</summary>
    MembershipEnds,

    /// <summary>The Owner as the only active member: the family goes with all of its records.</summary>
    HouseholdDeleted,

    /// <summary>The Owner while others remain: that would be an ownership transfer, deferred (ADR 0003 §6).</summary>
    RefusedOwnerWithMembers,

    /// <summary>The account's only family: "stop sharing everything" is account deletion.</summary>
    RefusedLastFamily,
}

public static class HouseholdLeaving
{
    /// <param name="members">The family's active members, the leaver included.</param>
    /// <param name="hasOtherFamily">Whether the leaver is an active member of another family.</param>
    public static LeaveDecision Decide(IReadOnlyCollection<Membership> members, UserId leaver, bool hasOtherFamily)
    {
        ArgumentNullException.ThrowIfNull(members);
        var me = members.SingleOrDefault(m => m.UserId == leaver)
            ?? throw new ArgumentException("The leaving user is not a member of this household.", nameof(leaver));
        if (me.Role == Role.Owner && members.Count > 1)
            return LeaveDecision.RefusedOwnerWithMembers;
        if (!hasOtherFamily)
            return LeaveDecision.RefusedLastFamily;
        return me.Role == Role.Owner ? LeaveDecision.HouseholdDeleted : LeaveDecision.MembershipEnds;
    }
}
