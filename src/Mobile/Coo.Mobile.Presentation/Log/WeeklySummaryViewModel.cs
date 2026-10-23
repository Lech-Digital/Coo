using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Advisor;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Log;

/// <summary>
/// "This week, in words" on Stats: the Advisor writes a few sentences comparing this week with the last, from the same
/// log summary it answers questions with. Only when asked (it uses the Advisor's daily allowance and its consent), and
/// kept until the log changes or the week turns.
/// </summary>
public sealed partial class WeeklySummaryViewModel(
    IAdvisorApi api, IHouseholdStore households, TimeProvider time, BuildAdvisorContext? buildContext = null) : ObservableObject
{
    private string? _forKey;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _isWriting;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Asked before the first summary, as before the first Advisor question; false stops.</summary>
    public Func<Task<bool>>? EnsureConsentAsync { get; set; }

    /// <summary>The log changed: a summary written before no longer matches it.</summary>
    public void Invalidate() => (_forKey, Text, IsError) = (null, "", false);

    [RelayCommand]
    private async Task Write()
    {
        if (IsWriting)
            return;
        if (EnsureConsentAsync is { } consent && !await consent())
            return;

        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        var baby = household is null ? null : (await households.ListBabiesAsync(household.Id, CancellationToken.None)).FirstOrDefault(b => !b.IsDeleted);
        if (household is null || baby is null)
            return;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var now = time.GetUtcNow();
        var key = $"{baby.Id.Value}:{System.Globalization.ISOWeek.GetWeekOfYear(TimeZoneInfo.ConvertTime(now, zone).DateTime)}:{Strings.Language}";
        if (key == _forKey && !IsError && Text.Length > 0)
            return;

        (IsWriting, IsError) = (true, false);
        try
        {
            var response = await api.AskAsync(
                new AskAdvisorRequest
                {
                    HouseholdId = household.Id.Value,
                    BabyId = baby.Id.Value,
                    // No name: the Advisor never sees the baby's name, only "the baby".
                    Question = Strings.Get("Summary.Prompt"),
                    TimeZoneId = zone.Id,
                    History = [],
                    Language = Strings.Language,
                    Context = buildContext is null ? null : await buildContext.ExecuteAsync(household.Id, baby.Id, zone, now, CancellationToken.None),
                },
                CancellationToken.None);
            var failed = response.ErrorCode is not null || string.IsNullOrWhiteSpace(response.Answer);
            (Text, IsError, _forKey) = (failed ? AdvisorViewModel.ErrorText(response.ErrorCode, response.Limit) : response.Answer!.Trim(), failed, failed ? null : key);
        }
#pragma warning disable CA1031 // A summary that cannot be written says so; it never breaks Stats.
        catch (Exception)
#pragma warning restore CA1031
        {
            (Text, IsError) = (Strings.Get("Advisor.Error.unavailable"), true);
        }
        finally
        {
            IsWriting = false;
        }
    }
}
