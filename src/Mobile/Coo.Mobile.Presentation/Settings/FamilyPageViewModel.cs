using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <param name="Label">The name as the row shows it: "Kasia (you)" for oneself.</param>
/// <param name="Name">The plain name, for the confirmation.</param>
public sealed record MemberRow(UserId UserId, string Label, string Email, string Role, bool CanRemove, string Name);

public sealed record InvitationRow(Guid Id, string Line);

/// <summary>
/// The Family page (Spec 011b §2), pushed from Settings → Family → Members. Offline it shows the last list fetched and says
/// it could not refresh; Remove, Withdraw, Invite and Leave then say "That didn't work…" and change nothing. Every command
/// ends in a catch-all and refuses what the page does not offer (the Owner, oneself, a Caregiver's remove), so a stale row
/// or a double tap never reaches the server.
/// </summary>
public sealed partial class FamilyPageViewModel(
    LoadFamilyMembers load, RemoveFamilyMember remove, WithdrawFamilyInvitation withdraw, InviteParent invite, LeaveFamily leave,
    Families families, IExternalLauncher launcher, IClockFormat clock) : ObservableObject
{
    private HouseholdId _household;
    private bool _canManage;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private IReadOnlyList<MemberRow> _members = [];

    [ObservableProperty]
    private IReadOnlyList<InvitationRow> _invitations = [];

    [ObservableProperty]
    private bool _showsInvitations;

    [ObservableProperty]
    private bool _showsInvite;

    [ObservableProperty]
    private bool _showsLeave;

    [ObservableProperty]
    private bool _showsDelete;

    [ObservableProperty]
    private bool _refreshFailed;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    /// <summary>
    /// Set when the server answered <c>household.confirmation-required</c> to a delete: the page asks this and, if accepted,
    /// runs <see cref="DeleteConfirmedCommand"/>. Null otherwise.
    /// </summary>
    [ObservableProperty]
    private ConfirmPrompt? _reconfirm;

    /// <summary>The family is gone from this phone (left or deleted): the page pops.</summary>
    public event EventHandler? Closed;

    public static string RefreshFailedText => Strings.Get("Family.RefreshFailed");

    /// <summary>Never throws: a failed local read leaves the rows as they were and says it could not refresh.</summary>
    public async Task LoadAsync(HouseholdId household)
    {
        try
        {
            await LoadCoreAsync(household);
        }
#pragma warning disable CA1031 // A failed local read must say so, never crash the page.
        catch (Exception)
#pragma warning restore CA1031
        {
            RefreshFailed = true;
        }
    }

    private async Task LoadCoreAsync(HouseholdId household)
    {
        _household = household;
        var list = await families.ListAsync(CancellationToken.None);
        var family = list.FirstOrDefault(f => f.Id == household);
        if (family is null)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Title = FamilyLabels.Title(family);
        var manage = family.CanManage;
        _canManage = manage;
        var hasOther = list.Count(f => f.IsShared) > 1;
        ShowsInvite = manage && !family.IsSoftLocked;
        ShowsInvitations = manage;
        ShowsLeave = hasOther && family.Membership?.Role is Role.Parent or Role.Caregiver;
        ShowsDelete = hasOther && family.Membership is { Role: Role.Owner, MemberCount: 1 };

        var loaded = await load.ExecuteAsync(household, CancellationToken.None);
        RefreshFailed = !loaded.IsFresh;
        if (loaded.Members is not { } members)
            return;
        Members = [.. members.Members.Select(m => new MemberRow(
            m.UserId, FamilyLabels.MemberName(m), m.Email, FamilyLabels.RoleText(m.Role), CanRemove: manage && !m.IsYou && m.Role != Role.Owner, m.Name))];
        Invitations = manage
            ? [.. members.Invitations.Select(i => new InvitationRow(i.InvitationId, Strings.Format("Family.InvitationLine", FamilyLabels.Until(i.ExpiresAt, clock.Is24Hour))))]
            : [];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The page binds to it on the instance.")]
    public ConfirmPrompt RemovePrompt(MemberRow member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new ConfirmPrompt(Strings.Get("Family.Remove"), Strings.Format("Family.RemoveConfirm", member.Name), Strings.Get("Family.Remove"), Strings.Get("Action.Cancel"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The page binds to it on the instance.")]
    public ConfirmPrompt WithdrawPrompt =>
        new(Strings.Get("Family.Withdraw"), Strings.Get("Family.WithdrawConfirm"), Strings.Get("Family.Withdraw"), Strings.Get("Action.Cancel"));

    public async Task<ConfirmPrompt> LeavePromptAsync() =>
        new(Strings.Get("Family.Leave"), await WithUnsharedAsync(Strings.Get("Family.LeaveConfirm")), Strings.Get("Family.LeaveShort"), Strings.Get("Action.Cancel"));

    public async Task<ConfirmPrompt> DeletePromptAsync()
    {
        var contents = await families.ContentsAsync(_household, CancellationToken.None);
        var main = contents.BabyNames.Count == 0
            ? Strings.Plural("Family.DeleteConfirmNoBaby", contents.EntryCount)
            : Strings.Plural("Family.DeleteConfirm", contents.EntryCount, string.Join(", ", contents.BabyNames));
        return new ConfirmPrompt(Strings.Get("Family.Delete"), await WithUnsharedAsync(main), Strings.Get("Family.DeleteShort"), Strings.Get("Action.Cancel"));
    }

    [RelayCommand]
    private Task RemoveAsync(MemberRow member) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(member);
        // Only an Owner or Parent removes, never the Owner or oneself: the row says so, and a stale row is refused here too.
        if (!_canManage || !member.CanRemove)
            return FamilyAction.Refused(HouseholdErrors.Forbidden);
        return await remove.ExecuteAsync(_household, member.UserId, CancellationToken.None);
    });

    [RelayCommand]
    private Task WithdrawAsync(InvitationRow invitation) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (!_canManage)
            return FamilyAction.Refused(HouseholdErrors.Forbidden);
        return await withdraw.ExecuteAsync(_household, invitation.Id, CancellationToken.None);
    });

    [RelayCommand]
    private Task InviteAsync() => RunAsync(async () =>
    {
        if (!ShowsInvite)
            return FamilyAction.Refused(HouseholdErrors.Forbidden);
        var result = await invite.ExecuteAsync(_household, CancellationToken.None);
        if (result.Invitation is { } created)
            await launcher.ShareTextAsync(FamilyLabels.ShareText(created, clock.Is24Hour), Strings.Get("Family.Invite"));
        return result.Action;
    });

    /// <summary>A non-Owner leaves (§8.3): the person has just confirmed, so <c>confirm: true</c>.</summary>
    [RelayCommand]
    private Task LeaveAsync() => LeaveOrDeleteAsync(allowed: ShowsLeave, confirm: true);

    /// <summary>
    /// The Owner alone deletes (§8.3): first with <c>confirm: false</c>, so an empty family goes at once and one that holds
    /// records answers <c>household.confirmation-required</c>, which sets <see cref="Reconfirm"/>.
    /// </summary>
    [RelayCommand]
    private Task DeleteAsync() => LeaveOrDeleteAsync(allowed: ShowsDelete, confirm: false);

    /// <summary>The delete again, after the server asked and the person accepted <see cref="Reconfirm"/>.</summary>
    [RelayCommand]
    private Task DeleteConfirmedAsync() => LeaveOrDeleteAsync(allowed: ShowsDelete, confirm: true);

    private async Task LeaveOrDeleteAsync(bool allowed, bool confirm)
    {
        var left = false;
        await RunAsync(async () =>
        {
            if (!allowed)
                return FamilyAction.Refused(HouseholdErrors.Forbidden);
            var action = await leave.ExecuteAsync(_household, confirm, CancellationToken.None);
            if (!confirm && action.Code == HouseholdErrors.ConfirmationRequired)
            {
                var again = await DeletePromptAsync();
                Reconfirm = again with { Message = Strings.Error(HouseholdErrors.ConfirmationRequired) + "\n\n" + again.Message };
                return FamilyAction.Done;
            }

            left = action.Succeeded;
            return action;
        }, reload: false);
        if (left)
            Closed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> WithUnsharedAsync(string message)
    {
        var unshared = (await families.ContentsAsync(_household, CancellationToken.None)).Unshared.Count;
        return unshared == 0 ? message : message + "\n\n" + Strings.Plural("Family.Unshared", unshared);
    }

    private async Task RunAsync(Func<Task<FamilyAction>> action, bool reload = true)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        Reconfirm = null;
        try
        {
            var result = await action();
            if (!result.Succeeded)
                Toast = result.Code is { } code ? Strings.Error(code) : Strings.Get("Family.ActionFailed");
        }
#pragma warning disable CA1031 // A failed local write or the share sheet refusing must say so, never crash the page.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
        }

        if (reload)
            await LoadAsync(_household);
    }
}
