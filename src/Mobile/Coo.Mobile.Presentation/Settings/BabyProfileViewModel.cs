using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>The baby page (Spec 009 §3.1): every field, avatar, one Save.</summary>
public sealed partial class BabyProfileViewModel : ObservableObject
{
    private readonly IHouseholdStore _households;
    private readonly UpdateBabyProfile _update;
    private readonly TimeProvider _time;
    private readonly IDevicePreferences? _preferences;
    private readonly IAdvisorApi? _advisorApi;
    private readonly IBillingService? _billing;

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

    [ObservableProperty]
    private string? _avatarRef;

    [ObservableProperty]
    private bool _isGeneratingAvatar;

    public bool IsPremium => _billing?.IsPremiumCached ?? false;

    public DateTime Today => _time.GetLocalNow().Date;

    public TimeProvider Time => _time;

    public event EventHandler? Saved;
    public event EventHandler? AvatarChanged;

    public BabyProfileViewModel(
        IHouseholdStore households,
        UpdateBabyProfile update,
        TimeProvider time,
        IDevicePreferences? preferences = null,
        IAdvisorApi? advisorApi = null,
        IBillingService? billing = null)
    {
        _households = households;
        _update = update;
        _time = time;
        _preferences = preferences;
        _advisorApi = advisorApi;
        _billing = billing;
    }

    public async Task LoadAsync()
    {
        // A household with no baby yet (a new one became active) has nothing to edit; Save then does nothing.
        var household = (await _households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        var babies = household is null ? [] : await _households.ListBabiesAsync(household.Id, CancellationToken.None);
        _baby = babies.Count > 0 ? babies[0] : null;
        if (_baby is null)
            return;
        Name = _baby.Name;
        Nicknames = string.Join(", ", _baby.Nicknames);
        IsBorn = _baby.IsBorn;
        BirthDate = (_baby.BirthDate ?? DateOnly.FromDateTime(Today)).ToDateTime(TimeOnly.MinValue);
        HasDueDate = _baby.DueDate is not null;
        DueDate = (_baby.DueDate ?? DateOnly.FromDateTime(Today)).ToDateTime(TimeOnly.MinValue);

        if (_preferences != null)
        {
            AvatarRef = await _preferences.GetBabyAvatarAsync(_baby.Id.Value, CancellationToken.None);
        }
    }

    [RelayCommand]
    public async Task SetPresetAvatar(string presetName)
    {
        if (_baby == null || _preferences == null)
            return;
        AvatarRef = $"preset:{presetName}";
        await _preferences.SetBabyAvatarAsync(_baby.Id.Value, AvatarRef, CancellationToken.None);
        AvatarChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task ClearAvatar()
    {
        if (_baby == null || _preferences == null)
            return;
        AvatarRef = null;
        await _preferences.SetBabyAvatarAsync(_baby.Id.Value, null, CancellationToken.None);
        AvatarChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLocalAvatarFileAsync(string filePath)
    {
        if (_baby == null || _preferences == null)
            return;
        AvatarRef = $"file:{filePath}";
        await _preferences.SetBabyAvatarAsync(_baby.Id.Value, AvatarRef, CancellationToken.None);
        AvatarChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> GenerateAiAvatarAsync(string imageBase64, string targetFilePath)
    {
        if (_baby == null || _preferences == null || _advisorApi == null)
            return false;

        IsGeneratingAvatar = true;
        try
        {
            var req = new GenerateAvatarRequest
            {
                HouseholdId = _baby.HouseholdId.Value,
                BabyId = _baby.Id.Value,
                ImageBase64 = imageBase64
            };

            var response = await _advisorApi.GenerateAvatarAsync(req, CancellationToken.None);
            if (!string.IsNullOrEmpty(response.ImageBase64))
            {
                var bytes = Convert.FromBase64String(response.ImageBase64);
                await File.WriteAllBytesAsync(targetFilePath, bytes);
                await SetLocalAvatarFileAsync(targetFilePath);
                return true;
            }

            if (response.ErrorCode == "premium_required")
            {
                Error = "Premium subscription required for AI avatar generation.";
            }
            return false;
        }
        catch (Exception ex)
        {
            Error = $"AI Avatar generation failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsGeneratingAvatar = false;
        }
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
        var result = await _update.ExecuteAsync(_baby, change, CancellationToken.None);
        Error = result.IsFailure ? Strings.Error(result.Error!.Code) : "";
        if (result.IsSuccess)
            Saved?.Invoke(this, EventArgs.Empty);
    }
}
