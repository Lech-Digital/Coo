using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public sealed record FamilyChoice(Coo.Domain.Common.HouseholdId Id, string Label);

/// <summary>How a family and its members read (Spec 011b §2, §7.3). Names join with ", " (ruling 11).</summary>
public static class FamilyLabels
{
    public static string Title(FamilyOnPhone family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family.BabyNames.Count == 0 ? Strings.Get("Family.NoBaby") : string.Join(", ", family.BabyNames);
    }

    public static string Second(FamilyOnPhone family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family.Membership switch
        {
            null => Strings.Get("Family.LocalOnly"),
            { Role: Role.Owner } => Strings.Get("Family.Yours"),
            { OwnerName.Length: > 0 } m => Strings.Format("Family.Of", m.OwnerName),
            _ => Strings.Get("Family.Title"),
        };
    }

    /// <summary>"Maja · Family of Andrzej": one line for the switcher's action sheet.</summary>
    public static string Choice(FamilyOnPhone family) => $"{Title(family)} · {Second(family)}";

    public static string RoleText(Role role) => Strings.Get(role switch
    {
        Role.Owner => "Family.RoleOwner",
        Role.Parent => "Family.RoleParent",
        _ => "Family.RoleCaregiver",
    });

    public static string MemberName(FamilyMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.IsYou ? Strings.Format("Family.You", member.Name) : member.Name;
    }

    /// <summary>"Thu 9 Oct, 14:02" in the person's language and clock.</summary>
    public static string Until(DateTimeOffset at, bool is24Hour) =>
        $"{at.ToLocalTime().ToString("ddd d MMM", Strings.Culture)}, {new Formats(is24Hour, VolumeUnit.Millilitres).Time(at)}";

    /// <summary>Spec 011b §3's share text: one resource, three placeholders.</summary>
    public static string ShareText(CreatedInvitation invitation, bool is24Hour)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return Strings.Format("Invite.ShareText", invitation.Link, invitation.Code.Display, Until(invitation.ExpiresAt, is24Hour));
    }
}
