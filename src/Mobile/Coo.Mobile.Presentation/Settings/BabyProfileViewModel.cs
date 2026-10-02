using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>The baby page (Spec 009 §3.1): every field, one Save.</summary>
public sealed partial class BabyProfileViewModel(IHouseholdStore households, UpdateBabyProfile update, TimeProvider time) : ObservableObject
{
    private BabyProfile? _baby;

    [ObservableProperty]
    private string _name = "";

    /// <summary>Comma-separated, as typed.</summary>
    [ObservableProperty]
    private string _nicknames = "";

    [ObservableProperty]
    private bool _isBorn;

    [ObservableProperty]
    private DateTime _birthDate;

    [ObservableProperty]
    private bool _hasDueDate;

    [ObservableProperty]
    private DateTime _dueDate;

    [ObservableProperty]
    private string _error = "";

    public DateTime Today => time.GetLocalNow().Date;

    public event EventHandler? Saved;

    public async Task LoadAsync()
    {
        // A household with no baby yet (a new one became active) has nothing to edit; Save then does nothing.
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        var babies = household is null ? [] : await households.ListBabiesAsync(household.Id, CancellationToken.None);
        _baby = babies.Count > 0 ? babies[0] : null;
        if (_baby is null)
            return;
        Name = _baby.Name;
        Nicknames = string.Join(", ", _baby.Nicknames);
        IsBorn = _baby.IsBorn;
        BirthDate = (_baby.BirthDate ?? DateOnly.FromDateTime(Today)).ToDateTime(TimeOnly.MinValue);
        HasDueDate = _baby.DueDate is not null;
        DueDate = (_baby.DueDate ?? DateOnly.FromDateTime(Today)).ToDateTime(TimeOnly.MinValue);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_baby is null)
            return;
        var nicknames = Nicknames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var change = new BabyProfileChange(
            Name, nicknames, IsBorn ? DateOnly.FromDateTime(BirthDate) : null,
            !IsBorn || HasDueDate ? DateOnly.FromDateTime(DueDate) : null);
        var result = await update.ExecuteAsync(_baby, change, CancellationToken.None);
        Error = result.IsFailure ? Strings.Error(result.Error!.Code) : "";
        if (result.IsSuccess)
            Saved?.Invoke(this, EventArgs.Empty);
    }
}
