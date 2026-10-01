using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Log;

public sealed record TotalItem(string KindCode, string Number, string Unit);

/// <summary>The Log screen (Spec 006 §7): one local day, with ribbon, totals and timeline.</summary>
public sealed partial class LogViewModel(IHouseholdStore households, GetDay getDay, IClockFormat clock, UndoService undo, TimeProvider time) : ObservableObject
{
    [ObservableProperty]
    private DateOnly _date = DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private IReadOnlyList<RibbonMark> _ribbon = [];

    [ObservableProperty]
    private IReadOnlyList<TotalItem> _totals = [];

    [ObservableProperty]
    private IReadOnlyList<TimelineItem> _rows = [];

    [ObservableProperty]
    private bool _canGoNext;

    public async Task RefreshAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return;
        var babies = await households.ListBabiesAsync(household.Id, CancellationToken.None);
        if (babies.Count == 0)
            return;
        var baby = babies[0];

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var now = time.GetLocalNow();
        var day = await getDay.ExecuteAsync(household.Id, baby.Id, Date, zone, now, CancellationToken.None);
        var formats = new Formats(clock.Is24Hour, household.VolumeUnit);
        var today = DateOnly.FromDateTime(now.DateTime);

        Title = Date == today ? Strings.Get("Section.Today") : Date.ToString("dddd d MMMM", Strings.Culture);
        CanGoNext = Date < today;
        Ribbon = day.Ribbon;
        var pumped = household.VolumeUnit == VolumeUnit.Ounces
            ? (Math.Round(day.Totals.PumpedMl / Coo.Domain.CareLog.Volume.MillilitresPerUsFluidOunce, 1).ToString("0.#", Strings.Culture), Strings.Get("Totals.PumpedOz"))
            : (Math.Round(day.Totals.PumpedMl).ToString("0", Strings.Culture), Strings.Get("Totals.PumpedMl"));
        // Hidden kinds have no totals; their entries stay in the timeline (Spec 009 §3.2).
        TotalItem[] totals =
        [
            new("feeding", day.Totals.Feeds.ToString(Strings.Culture), PluralWord("Totals.Feeds", day.Totals.Feeds)),
            new("diaper", day.Totals.Diapers.ToString(Strings.Culture), PluralWord("Totals.Diapers", day.Totals.Diapers)),
            new("sleep", Formats.Duration(day.Totals.Sleep), Strings.Get("Totals.Sleep")),
            new("pump", pumped.Item1, pumped.Item2),
        ];
        Totals = [.. totals.Where(t => KindVisibility.IsShown(household, t.KindCode))];
        Rows = [.. day.Entries.Where(e => e.Id != undo.PendingDelete).Select(e => new TimelineItem(e.Id, formats.Time(e.Start), e.KindCode, formats.Title(e, now), Strings.Get("Who.You")))];
    }

    [RelayCommand]
    private Task Previous()
    {
        Date = Date.AddDays(-1);
        return RefreshAsync();
    }

    [RelayCommand]
    private Task Next()
    {
        if (!CanGoNext)
            return Task.CompletedTask;
        Date = Date.AddDays(1);
        return RefreshAsync();
    }

    private static string PluralWord(string key, int n) =>
        Strings.Get($"{key}.{PluralRules.Select(Strings.Language, n)}");
}
