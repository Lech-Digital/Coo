using Coo.Contracts.Auth;
using Coo.Domain.Households;

namespace Coo.Mobile.Infrastructure;

/// <summary>The one place a role's wire and storage text (<see cref="AuthRoles"/>) becomes a <see cref="Role"/> and back.</summary>
internal static class RoleNames
{
    /// <summary>Null for text this build does not know: callers decide whether that is skipped, a failure or unusable.</summary>
    public static Role? Parse(string? value) => value switch
    {
        AuthRoles.Owner => Role.Owner,
        AuthRoles.Parent => Role.Parent,
        AuthRoles.Caregiver => Role.Caregiver,
        _ => null,
    };

    public static string Text(Role role) => role switch
    {
        Role.Owner => AuthRoles.Owner,
        Role.Parent => AuthRoles.Parent,
        Role.Caregiver => AuthRoles.Caregiver,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };
}
