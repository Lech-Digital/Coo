using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>What a member may do in a household (ADR 0003 §6). Every role may log and view.</summary>
public enum Role
{
    /// <summary>The creator; exactly one per household.</summary>
    Owner,

    Parent,

    /// <summary>For example a nanny or a daycare: logs and views, nothing else.</summary>
    Caregiver,

    /// <summary>For example a grandparent: follows the log, logs nothing.</summary>
    Viewer,
}

/// <summary>
/// A user's place in a household (ADR 0003 §6). A user may belong to several households (the nanny case). Increment 11b
/// adds Parents who join by invitation.
/// </summary>
public sealed class Membership
{
    private Membership(HouseholdId householdId, UserId userId, Role role, DateTimeOffset joinedAt)
    {
        HouseholdId = householdId;
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
    }

    public HouseholdId HouseholdId { get; }

    public UserId UserId { get; }

    public Role Role { get; }

    public DateTimeOffset JoinedAt { get; }

    /// <summary>An Owner or a Parent: "a parent" in ADR 0003 §8's deletion rules.</summary>
    public bool IsParent => Role is Role.Owner or Role.Parent;

    /// <summary>Writes to the care log: everyone but a <see cref="Role.Viewer"/>.</summary>
    public bool CanLog => Role != Role.Viewer;

    public bool CanInvite => IsParent;

    public bool CanManageBaby => IsParent;

    public bool CanManageBilling => IsParent;

    public static Membership Create(HouseholdId householdId, UserId userId, Role role, DateTimeOffset joinedAt) =>
        Enum.IsDefined(role)
            ? new Membership(householdId, userId, role, joinedAt)
            : throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role.");

    /// <summary>An Owner or Parent may remove another member of the same household, except the Owner.</summary>
    public bool CanRemove(Membership other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsParent && other.HouseholdId == HouseholdId && other.UserId != UserId && other.Role != Role.Owner;
    }

    /// <summary>An Owner or Parent may withdraw any open invitation of the same household, not only their own (Spec 011b §15.2 Q3).</summary>
    public bool CanWithdraw(Invitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return IsParent && invitation.HouseholdId == HouseholdId;
    }
}
