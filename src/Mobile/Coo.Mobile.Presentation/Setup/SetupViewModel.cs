using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Setup;

/// <summary>First launch (Spec 006 §3, §3a): the baby, born or expected. Everything else has a default.</summary>
public sealed partial class SetupViewModel(IHouseholdStore households, SetUpBaby setUpBaby, TimeProvider time) : ObservableObject
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

    public DateTime Today => time.GetLocalNow().Date;

    /// <summary>Raised when the baby is saved; the host then shows Today.</summary>
    public event EventHandler? Completed;

    /// <summary>"Have an invitation? Enter a code" (Spec 011b §4.4): the host opens the code sheet, then the join flow.</summary>
    public event EventHandler? EnterCodeRequested;

    public static string HaveInvitationText => Strings.Get("Setup.HaveInvitation");

    [RelayCommand]
    private void EnterCode() => EnterCodeRequested?.Invoke(this, EventArgs.Empty);

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
