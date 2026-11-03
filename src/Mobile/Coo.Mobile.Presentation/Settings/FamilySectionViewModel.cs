using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>What the family switcher offers for a family that is not shown (walk fix F2).</summary>
public enum FamilyRowAction
{
    /// <summary>Its entries into the shown family: Spec 011b's bring-over, where §6.3 allows it.</summary>
    BringHere,

    /// <summary>A family on this phone only: its log goes from the phone after a confirmation.</summary>
    RemoveFromPhone,

    /// <summary>An account family the person may leave: the Family page's leave.</summary>
    Leave,

    /// <summary>An account family the person owns alone: the Family page's delete.</summary>
    Delete,
}

/// <summary>A row of the family switcher: the label as before ("Lena · Your family"), the shown one marked and not tappable.</summary>
/// <param name="Title">The children: "Maja", "Maja, Tom", or "No baby yet".</param>
/// <param name="Detail">Whose family and the person's place in it: "Your family", "Family of Kasia · Caregiver", "On this phone only".</param>
public sealed record FamilyRow(HouseholdId Id, string Label, bool IsShown, IReadOnlyList<FamilyRowAction> Actions, string Title = "", string Detail = "");

/// <summary>
/// Settings → Family (Spec 011b §2), right after Account: which family is shown, its members, inviting, joining and bringing
/// entries over. Pure: the page asks it and draws; texts are read on each use.
/// </summary>
public sealed partial class FamilySectionViewModel(
    Families families, Account account, SignIn signIn, ConsentGate consent, ISessionStore sessions, IConsentPrompt consentPrompt,
    InviteParent invite, IExternalLauncher launcher, IClockFormat clock, ISyncControl sync) : ObservableObject
{
    [ObservableProperty]
    private bool _showsSwitcher;

    [ObservableProperty]
    private string _showingTitle = "";

    [ObservableProperty]
    private string _showingLine = "";

    [ObservableProperty]
    private bool _showsMembers;

    [ObservableProperty]
    private string _membersLine = "";

    [ObservableProperty]
    private bool _showsOnlyHere;

    [ObservableProperty]
    private bool _showsInvite;

    [ObservableProperty]
    private bool _showsBringOver;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    [ObservableProperty]
    private IReadOnlyList<FamilyChoice> _choices = [];

    [ObservableProperty]
    private IReadOnlyList<FamilyRow> _rows = [];

    private IReadOnlyList<FamilyOnPhone> _list = [];

    /// <summary>The shown family, for the Family page and the bring-over.</summary>
    public HouseholdId? ShownId { get; private set; }

    /// <summary>Raises PropertyChanged on the caller's thread: pages dispatch before calling it from Account.Changed or DataChanged.</summary>
    public async Task RefreshAsync()
    {
        var list = await families.ListAsync(CancellationToken.None);
        var shown = list.FirstOrDefault(f => f.IsShown);
        ShownId = shown?.Id;
        ShowsSwitcher = list.Count > 1;
        ShowingTitle = shown is null ? "" : Strings.Format("Family.Showing", FamilyLabels.Title(shown));
        ShowingLine = shown is null ? "" : FamilyLabels.Second(shown);
        ShowsMembers = shown is { IsShared: true };
        MembersLine = shown?.Membership is { } m ? Strings.Plural("Family.People", m.MemberCount) : "";
        ShowsOnlyHere = shown is { IsShared: false };
        ShowsInvite = account.State is SignedOut || shown is { IsShared: true, IsSoftLocked: false, CanManage: true };
        ShowsBringOver = list.Count > 1 && shown is { MayBeBroughtOver: true }
            && list.Any(f => f.Id != shown.Id && f.IsShared && !f.IsSoftLocked);
        Choices = [.. list.Where(f => !f.IsSoftLocked).Select(f => new FamilyChoice(f.Id, FamilyLabels.Choice(f)))];
        var shared = list.Count(f => f.IsShared);
        Rows = [.. list.Where(f => !f.IsSoftLocked).Select(f => new FamilyRow(
            f.Id, FamilyLabels.Choice(f), f.IsShown, f.IsShown || shown is null ? [] : ActionsFor(f, shown, shared),
            FamilyLabels.Title(f), FamilyLabels.Detail(f)))];
        _list = list;
    }

    public static string SwitcherTitle => Strings.Get("Family.Switch");

    public static string CancelText => Strings.Get("Action.Cancel");

    public static string ActionText(FamilyRowAction action) => Strings.Get(action switch
    {
        FamilyRowAction.BringHere => "Family.BringHere",
        FamilyRowAction.RemoveFromPhone => "Family.RemoveFromPhone",
        FamilyRowAction.Leave => "Family.Leave",
        _ => "Family.Delete",
    });

    /// <summary>
    /// Bring here only from a family §6.3 lets go (never synced, or the account's) into a shown account family; remove from
    /// the phone only a family that is not the account's; leave or delete as the Family page offers them (another account
    /// family is kept).
    /// </summary>
    private static List<FamilyRowAction> ActionsFor(FamilyOnPhone family, FamilyOnPhone shown, int sharedCount)
    {
        var actions = new List<FamilyRowAction>(2);
        if (family.MayBeBroughtOver && shown is { IsShared: true, IsSoftLocked: false })
            actions.Add(FamilyRowAction.BringHere);
        if (!family.IsShared)
        {
            if (!family.IsHome)
                actions.Add(FamilyRowAction.RemoveFromPhone);
        }
        else if (sharedCount > 1 && family.Membership?.Role is Role.Parent or Role.Caregiver)
        {
            actions.Add(FamilyRowAction.Leave);
        }
        else if (sharedCount > 1 && family.Membership is { Role: Role.Owner, MemberCount: 1 })
        {
            actions.Add(FamilyRowAction.Delete);
        }

        return actions;
    }

    /// <summary>
    /// "Remove Lena's log from this phone?": gone for a family that never synced; kept with the account it was shared from
    /// otherwise, with what was not shared yet.
    /// </summary>
    public async Task<ConfirmPrompt> RemovePromptAsync(FamilyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var family = _list.FirstOrDefault(f => f.Id == row.Id);
        var synced = family?.HasEverSynced == true;
        var names = family?.BabyNames ?? [];
        var key = synced ? "Family.RemoveFromPhoneKept" : "Family.RemoveFromPhoneGone";
        var message = names.Count == 0 ? Strings.Get(key + "NoBaby") : Strings.Format(key, string.Join(", ", names));
        if (synced && (await families.ContentsAsync(row.Id, CancellationToken.None)).Unshared.Count is > 0 and var unshared)
            message += "\n\n" + Strings.Plural("Family.Unshared", unshared);
        return new ConfirmPrompt(Strings.Get("Family.RemoveFromPhone"), message, Strings.Get("Family.Remove"), Strings.Get("Action.Cancel"));
    }

    /// <summary>
    /// Takes a family that is on this phone only off it (walk fix F2), with no cycle in flight, as every whole-family change on
    /// the phone. Never the shown family, an account family or the account's home: a stale row is refused.
    /// </summary>
    [RelayCommand]
    private async Task RemoveFromPhoneAsync(FamilyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var family = (await families.ListAsync(CancellationToken.None)).FirstOrDefault(f => f.Id == row.Id);
            if (family is { IsShown: false, IsShared: false, IsHome: false })
            {
                await sync.RunExclusiveAsync(ct => families.RemoveFromPhoneAsync(row.Id, ct), CancellationToken.None);
                sync.NotifyDataChanged();
            }
            else if (family is not null)
            {
                Toast = Strings.Get("Family.ActionFailed");
            }
        }
#pragma warning disable CA1031 // A failed local write must say so, never crash Settings.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
            try
            {
                await RefreshAsync();
            }
#pragma warning disable CA1031 // The rows keep what they showed; a failed read must not escape the command.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <summary>The switcher (§7.3): only the shown family changes; Today, Log and Settings follow.</summary>
    [RelayCommand]
    private async Task SwitchAsync(FamilyChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        try
        {
            await families.SwitchAsync(choice.Id, CancellationToken.None);
            sync.NotifyDataChanged();
            await RefreshAsync();
        }
#pragma warning disable CA1031 // A failed local write must say so, never crash Settings.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
    }

    /// <summary>§3: signed out, sign-in opens in place, then consent if the account has none, then the share sheet.</summary>
    [RelayCommand]
    private async Task InviteAsync(Role role)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var justSignedIn = false;
            if (account.State is SignedOut)
            {
                switch (await signIn.ExecuteAsync(CancellationToken.None))
                {
                    case SignInResult.Cancelled:
                        return;
                    case SignInResult.Failed:
                        Toast = Strings.Get("Account.SignInFailed");
                        return;
                }

                justSignedIn = true;
            }

            if (await sessions.GetAsync(CancellationToken.None) is not { } session)
                return;
            if (await consent.CheckAsync(session.Profile.UserId, CancellationToken.None) != ConsentState.Given && await consentPrompt.AskAsync() != ConsentAnswer.Given)
                return;

            await families.RefreshAsync(CancellationToken.None);
            var shown = await families.ShownAsync(CancellationToken.None);

            // Ruling 28: after signing in, the account's own family; otherwise the shown one, which the row was for.
            if (!justSignedIn && shown is { IsShared: true, CanManage: false })
                return;
            var target = !justSignedIn && shown is { IsShared: true } ? shown.Id : session.AccountHousehold;
            var result = await invite.ExecuteAsync(target, role, CancellationToken.None);
            if (result.Invitation is { } invitation)
                await launcher.ShareTextAsync(FamilyLabels.ShareText(invitation, clock.Is24Hour), Strings.Get("Family.Invite"));
            else
                Toast = result.Action.Code is { } code ? Strings.Error(code) : Strings.Get("Family.ActionFailed");
        }
#pragma warning disable CA1031 // A failed local read or the share sheet refusing must say so, never crash Settings.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
            try
            {
                await RefreshAsync();
            }
#pragma warning disable CA1031 // The rows keep what they showed; a failed read must not escape the command.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
