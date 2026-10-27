using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Setup;

/// <summary>First launch (Spec 006 §3, §3a): the baby, born or expected. Everything else has a default.</summary>
/// <param name="signIn">"Already use Coo? Sign in": null where it is not offered (tests).</param>
/// <param name="sync">Pulls the account's family right after that sign-in.</param>
public sealed partial class SetupViewModel(
    IHouseholdStore households, SetUpBaby setUpBaby, TimeProvider time, Account account, SignIn? signIn = null,
    Coo.Mobile.Application.Sync.ISyncControl? sync = null) : ObservableObject
{
    [ObservableProperty]
    private bool _hasArrived = true;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private DateTime _birthDate = time.GetLocalNow().Date;

    [ObservableProperty]
    private bool _bornEarly;

    [ObservableProperty]
    private DateTime _dueDate = time.GetLocalNow().Date.AddDays(60);

    [ObservableProperty]
    private string _error = "";

    /// <summary>After "Sign in" here: what happens next (the family's log is on its way), or what to do.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isSigningIn;

    public DateTime Today => time.GetLocalNow().Date;

    /// <summary>Raised when the baby is saved; the host then shows Today.</summary>
    public event EventHandler? Completed;

    /// <summary>"Have an invitation? Enter a code" (Spec 011b §4.4): the host opens the code sheet, then the join flow.</summary>
    public event EventHandler? EnterCodeRequested;

    public static string HaveInvitationText => Strings.Get("Setup.HaveInvitation");

    public static string SignInText => Strings.Get("Setup.SignIn");

    /// <summary>Offered while signed out: someone who already has a family (a new phone, a reinstall) goes to it, not to a new baby.</summary>
    public bool CanSignIn => signIn is not null && account.State is SignedOut && !IsSigningIn;

    /// <summary>
    /// The subtitle under "Welcome to Coo" (walk fix F5): signed out, no account is needed; signed in (a removal made a new family,
    /// or an account's first sign-in), the person already has an account, so it only says what to do.
    /// </summary>
    public string Hint => Strings.Get(account.State is SignedOut ? "Setup.Hint" : "Setup.HintSignedIn");

    [RelayCommand]
    private void EnterCode() => EnterCodeRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The same sign-in as Settings. An account with a family elsewhere pulls it, and the host replaces this step with Today
    /// once the family's baby is on the phone; an account without one stays here to add the baby.
    /// </summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (signIn is null || IsSigningIn)
            return;
        IsSigningIn = true;
        Error = "";
        OnPropertyChanged(nameof(CanSignIn));
        try
        {
            switch (await signIn.ExecuteAsync(CancellationToken.None))
            {
                case SignInResult.SignedIn:
                    Status = Strings.Get(account.State is JoinPending ? "Setup.Fetching" : "Setup.HintSignedIn");
                    sync?.RequestSync();
                    break;
                case SignInResult.Failed:
                    Error = Strings.Get("Account.SignInFailed");
                    break;
            }
        }
#pragma warning disable CA1031 // A sign-in that throws is "didn't work", never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Strings.Get("Account.SignInFailed");
        }
        finally
        {
            IsSigningIn = false;
            OnPropertyChanged(nameof(CanSignIn));
        }
    }

    [RelayCommand]
    private async Task Continue()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).First(h => !h.IsDeleted);
        var name = string.IsNullOrWhiteSpace(Name) && !HasArrived ? Strings.Get("Setup.DefaultName") : Name;
        DateOnly? birth = HasArrived ? DateOnly.FromDateTime(BirthDate) : null;
        DateOnly? due = !HasArrived || BornEarly ? DateOnly.FromDateTime(DueDate) : null;

        var result = await setUpBaby.ExecuteAsync(household.Id, name, birth, due, CancellationToken.None);
        if (result.IsFailure)
        {
            Error = Strings.Error(result.Error!.Code);
            return;
        }

        Error = "";
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
