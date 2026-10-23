using Coo.Domain.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Pages;

/// <summary>"Who are you inviting?": a parent, a nanny or daycare who logs, or family who follow along.</summary>
internal static class InviteRole
{
    public static async Task<Role?> AskAsync(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var parent = Strings.Get("Invite.AsParent");
        var caregiver = Strings.Get("Invite.AsCaregiver");
        var viewer = Strings.Get("Invite.AsViewer");
        var chosen = await page.DisplayActionSheetAsync(Strings.Get("Invite.Who"), Strings.Get("Action.Cancel"), null, parent, caregiver, viewer);
        return chosen == parent ? Role.Parent : chosen == caregiver ? Role.Caregiver : chosen == viewer ? Role.Viewer : null;
    }
}
