using Coo.Domain.Households;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>How a role is stored in <c>memberships.role</c>; the check constraint allows exactly these.</summary>
internal static class RoleText
{
    public const string Owner = "owner";
    public const string Parent = "parent";
    public const string Caregiver = "caregiver";

    public static Role Parse(string value) => value switch
    {
        Owner => Role.Owner,
        Parent => Role.Parent,
        Caregiver => Role.Caregiver,
        _ => throw new InvalidOperationException($"Unknown role '{value}' in memberships."),
    };
}
