using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>A yes/no question the page asks with Android's dialog.</summary>
public sealed record ConfirmPrompt(string Title, string Message, string Accept, string Cancel);

/// <summary>
/// The Account section at the top of Settings (Spec 010 §2). It follows <see cref="Account"/>, so a refused session
/// found in the background shows here too. Texts are read on each use, so a language change needs no reload.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    private readonly Account _account;
    private readonly SignIn _signIn;
    private readonly SignOutOfPhone _signOut;
    private readonly DeleteAccount _deleteAccount;
    private readonly RefreshAccount _refresh;
    private readonly IHouseholdStore _households;
    private readonly ISyncStatus _sync;
    private readonly IClockFormat _clock;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(Name), nameof(Email), nameof(StatusLine), nameof(LineAction), nameof(LineActionText))]
    private AccountState _state = new SignedOut(SessionExpired: false);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public AccountViewModel(
        Account account, SignIn signIn, SignOutOfPhone signOut, DeleteAccount deleteAccount, RefreshAccount refresh, IHouseholdStore households,
        ISyncStatus sync, IClockFormat clock)
    {
        _account = account;
        _signIn = signIn;
        _signOut = signOut;
        _deleteAccount = deleteAccount;
        _refresh = refresh;
        _households = households;
        _sync = sync;
        _clock = clock;
        _account.Changed += OnAccountChanged;
        _sync.StatusChanged += OnSyncChanged;
        State = account.State;
    }

    public bool IsSignedIn => State is not SignedOut;

    public string Name => Profile?.Name ?? "";

    public string Email => Profile?.Email ?? "";

    /// <summary>The line under "Sign in with Google", or the sync line under the account (Spec 011 §2).</summary>
    public string StatusLine => Line.Text;

    public SyncLineAction LineAction => Line.Action;

    /// <summary>The button under the line: "Turn on sharing" or "Choose what to do with them"; empty for none.</summary>
    public string LineActionText => LineAction switch
    {
        SyncLineAction.TurnOnSharing => Strings.Get("Sync.TurnOn"),
        SyncLineAction.ChooseWhatToDo => Strings.Get("Account.ChooseWhatToDo"),
        _ => "",
    };

    /// <summary>The page opens the consent sheet (Spec 011 §3).</summary>
    public event EventHandler? ConsentRequested;

    /// <summary>The page opens the bring-over sheet (Spec 011 §4).</summary>
    public event EventHandler? BringOverRequested;

    private SyncLineView Line => SyncLine.For(State, _sync.Status, _clock.Is24Hour);

    /// <summary>Written by hand rather than with <c>[RelayCommand]</c>: the generated name would clash with <see cref="LineAction"/>.</summary>
    public IRelayCommand LineActionCommand => _lineActionCommand ??= new RelayCommand(() =>
    {
        switch (LineAction)
        {
            case SyncLineAction.TurnOnSharing:
                ConsentRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SyncLineAction.ChooseWhatToDo:
                BringOverRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    });

    private IRelayCommand? _lineActionCommand;

    // Raised from the engine's background thread; the page dispatches (SettingsPage.OnAccountChanged).
    private void OnSyncChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(LineAction));
        OnPropertyChanged(nameof(LineActionText));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The page binds to it on the instance.")]
    /// <summary>
    /// The sign-out confirmation, after a last push: the account's families leave the phone, and entries that never reached the
    /// server would go with them, so their number is said when there are any.
    /// </summary>
    public async Task<ConfirmPrompt> SignOutPromptAsync()
    {
        var unsent = await _signOut.UnsentAfterPushAsync(CancellationToken.None);
        var message = unsent > 0 ? Strings.Plural("Account.SignOutUnsent", unsent) : Strings.Get("Account.SignOutConfirm");
        return new(Strings.Get("Account.SignOut"), message, Strings.Get("Account.SignOut"), Strings.Get("Action.Cancel"));
    }

    private AccountProfile? Profile => State switch
    {
        SignedIn s => s.Profile,
        JoinPending j => j.Profile,
        _ => null,
    };

    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        _sync.StatusChanged -= OnSyncChanged;
    }

    /// <summary>Settings opened: the latest name and email from the server. Offline, nothing changes.</summary>
    public Task RefreshAsync() => _refresh.ExecuteAsync(CancellationToken.None);

    /// <summary>
    /// The deletion confirmation names the baby whose shared log goes (Spec 010 §5). A join-pending phone has never seen
    /// the account's household, so it names none.
    /// </summary>
    public async Task<ConfirmPrompt> DeletePromptAsync()
    {
        var names = new List<string>();
        if (State is not JoinPending)
        {
            var household = (await _households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
            if (household is not null)
                names.AddRange((await _households.ListBabiesAsync(household.Id, CancellationToken.None)).Where(b => !b.IsDeleted).Select(b => b.Name));
        }

        var message = names.Count == 0
            ? Strings.Get("Account.DeleteConfirmNoBaby")
            : Strings.Format("Account.DeleteConfirm", string.Join(", ", names));
        return new ConfirmPrompt(Strings.Get("Account.Delete"), message, Strings.Get("Account.Delete"), Strings.Get("Action.Cancel"));
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            if (await _signIn.ExecuteAsync(CancellationToken.None) == SignInResult.Failed)
                Toast = Strings.Get("Account.SignInFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task SignOutAsync() => _signOut.ExecuteAsync(CancellationToken.None);

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            if (!await _deleteAccount.ExecuteAsync(CancellationToken.None))
                Toast = Strings.Get("Account.DeleteFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e) => State = _account.State;
}
