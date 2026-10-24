using Coo.Domain.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Pages;

/// <summary>"Who are you inviting?": a parent, a nanny or daycare who logs, or family who follow along.</summary>
internal static class InviteRole
{
    public static async Task<Role?> AskAsync(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Role[] roles = [Role.Parent, Role.Caregiver, Role.Viewer];
        var chosen = await Components.CooDialog.ChooseAsync(
            page, Strings.Get("Invite.Who"), [Strings.Get("Invite.AsParent"), Strings.Get("Invite.AsCaregiver"), Strings.Get("Invite.AsViewer")]);
        return chosen >= 0 ? roles[chosen] : null;
    }
}
