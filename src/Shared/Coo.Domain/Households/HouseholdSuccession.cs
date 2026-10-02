using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>What happens to a household when one of its members leaves (ADR 0003 §8).</summary>
public abstract record Succession;

/// <summary>The household goes on as it is.</summary>
public sealed record HouseholdRemains : Succession;

/// <summary>The leaver was the last parent: the household and all of its care data go.</summary>
public sealed record HouseholdDeleted : Succession;

/// <summary>The Owner leaves while parents remain: the parent who joined earliest becomes the Owner.</summary>
public sealed record OwnershipPasses(UserId NewOwner) : Succession;

public static class HouseholdSuccession
{
    private static readonly IComparer<Guid> ByGuidOrder = Comparer<Guid>.Create(GuidOrder.Compare);

    /// <summary>
    /// The ownership transfer is decided before any deletion: a household is deleted only when no Owner or Parent
    /// would remain.
    /// </summary>
    public static Succession WhenLeaving(IReadOnlyCollection<Membership> members, UserId leaving)
    {
        ArgumentNullException.ThrowIfNull(members);
        var leaver = members.SingleOrDefault(m => m.UserId == leaving)
            ?? throw new ArgumentException("The leaving user is not a member of this household.", nameof(leaving));

        var next = members
            .Where(m => m.UserId != leaving && m.IsParent)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.UserId.Value, ByGuidOrder)
            .FirstOrDefault();

        if (next is null)
            return new HouseholdDeleted();
        return leaver.Role == Role.Owner ? new OwnershipPasses(next.UserId) : new HouseholdRemains();
    }
}
