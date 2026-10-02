using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
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
    private readonly SignOut _signOut;
    private readonly DeleteAccount _deleteAccount;
    private readonly RefreshAccount _refresh;
    private readonly IHouseholdStore _households;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(Name), nameof(Email), nameof(StatusLine))]
    private AccountState _state = new SignedOut(SessionExpired: false);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public AccountViewModel(
        Account account, SignIn signIn, SignOut signOut, DeleteAccount deleteAccount, RefreshAccount refresh, IHouseholdStore households)
    {
        _account = account;
        _signIn = signIn;
        _signOut = signOut;
        _deleteAccount = deleteAccount;
        _refresh = refresh;
        _households = households;
        _account.Changed += OnAccountChanged;
        State = account.State;
    }

    public bool IsSignedIn => State is not SignedOut;

    public string Name => Profile?.Name ?? "";

    public string Email => Profile?.Email ?? "";

    /// <summary>The line under "Sign in with Google", or under the account.</summary>
    public string StatusLine => State switch
    {
        SignedOut { SessionExpired: true } => Strings.Get("Account.SessionExpired"),
        SignedOut => Strings.Get("Account.SignInHint"),
        JoinPending => Strings.Get("Account.JoinPending"),
        _ => "",
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The page binds to it on the instance.")]
    public ConfirmPrompt SignOutPrompt =>
        new(Strings.Get("Account.SignOut"), Strings.Get("Account.SignOutConfirm"), Strings.Get("Account.SignOut"), Strings.Get("Action.Cancel"));

    private AccountProfile? Profile => State switch
    {
        SignedIn s => s.Profile,
        JoinPending j => j.Profile,
        _ => null,
    };

    public void Dispose() => _account.Changed -= OnAccountChanged;

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
        if (!await _deleteAccount.ExecuteAsync(CancellationToken.None))
            Toast = Strings.Get("Account.DeleteFailed");
    }

    private void OnAccountChanged(object? sender, EventArgs e) => State = _account.State;
}
