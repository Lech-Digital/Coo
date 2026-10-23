using Coo.Domain.Common;

namespace Coo.Domain.Households;

public readonly record struct InvitationId
{
    public InvitationId(Guid value) =>
        Value = value == Guid.Empty ? throw new ArgumentException("An ID cannot be Guid.Empty.", nameof(value)) : value;

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// An invitation to join a family (Spec 011b §3, ADR 0003 §6): valid for 72 hours, used once, withdrawable. The code
/// itself is never kept here: the server stores only its hash.
/// </summary>
public sealed class Invitation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>One code for unknown, expired, used, withdrawn and gone: no oracle (spec §5.2).</summary>
    public const string InvalidCode = "invitation.invalid";

    private Invitation(
        InvitationId id, HouseholdId householdId, Role role, UserId createdBy, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        UserId? acceptedBy, DateTimeOffset? acceptedAt, DateTimeOffset? revokedAt)
    {
        Id = id;
        HouseholdId = householdId;
        Role = role;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        AcceptedBy = acceptedBy;
        AcceptedAt = acceptedAt;
        RevokedAt = revokedAt;
    }

    public InvitationId Id { get; }

    public HouseholdId HouseholdId { get; }

    public Role Role { get; }

    public UserId CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public UserId? AcceptedBy { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Neither used nor withdrawn (it may still have expired).</summary>
    public bool IsOpen => AcceptedAt is null && RevokedAt is null;

    public static Invitation Create(InvitationId id, HouseholdId household, Role role, UserId createdBy, DateTimeOffset now) =>
        role is Role.Parent or Role.Caregiver or Role.Viewer
            ? new Invitation(id, household, role, createdBy, now, now + Lifetime, null, null, null)
            : throw new ArgumentOutOfRangeException(nameof(role), role, "An invitation is for a Parent, a Caregiver or a Viewer.");

    internal static Invitation Rehydrate(
        InvitationId id, HouseholdId household, Role role, UserId createdBy, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        UserId? acceptedBy, DateTimeOffset? acceptedAt, DateTimeOffset? revokedAt) =>
        new(id, household, role, createdBy, createdAt, expiresAt, acceptedBy, acceptedAt, revokedAt);

    /// <summary>Open and before its expiry; at exactly 72 hours it is no longer usable.</summary>
    public bool IsUsable(DateTimeOffset now) => IsOpen && now < ExpiresAt;

    public bool WasAcceptedBy(UserId user) => AcceptedAt is not null && AcceptedBy == user;

    public Result Accept(UserId user, DateTimeOffset now)
    {
        if (!IsUsable(now))
            return Result.Failure(InvalidCode);
        AcceptedBy = user;
        AcceptedAt = now;
        return Result.Success;
    }

    /// <summary>Only an open invitation is withdrawn; a used or already withdrawn one stays as it was.</summary>
    public void Withdraw(DateTimeOffset now)
    {
        if (IsOpen)
            RevokedAt = now;
    }
}
