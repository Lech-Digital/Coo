using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

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
    private async Task InviteAsync()
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
            var result = await invite.ExecuteAsync(target, CancellationToken.None);
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
