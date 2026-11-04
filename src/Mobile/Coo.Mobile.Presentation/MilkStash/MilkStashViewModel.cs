using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.MilkStash;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.MilkStash;

/// <summary>One bag on the stash page: "120 ml · Fridge", "Expressed Mon 08:30 · use by Fri 08:30", and how fresh it is.</summary>
/// <param name="Number">"#2", the number on the container; empty for a bag without one.</param>
public sealed record MilkBagRow(
    EntryId Id, string Amount, string Where, string Dates, MilkFreshness Freshness, MilkPlace Place, bool IsThawed, string? Label, string Number = "");

/// <summary>
/// The milk stash: the fridge and freezer stock, the bag to use first at the top, use-by warnings, and adding bags. Anyone
/// who logs can change it (owners, parents, caregivers); viewers see it.
/// </summary>
public sealed partial class MilkStashViewModel(
    IHouseholdStore households, GetMilkStash get, AddMilkBag add, ChangeMilkBag change, ILogAccess access, IClockFormat clock, TimeProvider time)
    : ObservableObject
{
    private HouseholdId? _household;
    private Formats _formats = new(true, VolumeUnit.Millilitres);

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _canChange;

    [ObservableProperty]
    private IReadOnlyList<MilkBagRow> _bags = [];

    /// <summary>"Fridge 210 ml · Freezer 1,050 ml".</summary>
    [ObservableProperty]
    private string _totals = "";

    /// <summary>"1 bag to use soon · 1 past its use-by"; empty when all is fine.</summary>
    [ObservableProperty]
    private string _warning = "";

    /// <summary>The amount being added, in the family's unit.</summary>
    [ObservableProperty]
    private string _amount = "";

    [ObservableProperty]
    private MilkPlace _place = MilkPlace.Fridge;

    [ObservableProperty]
    private DateTime _expressedDate;

    [ObservableProperty]
    private TimeSpan _expressedTime;

    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _error = "";

    /// <summary>The bag being edited on the page; null when none is.</summary>
    [ObservableProperty]
    private EntryId? _editing;

    /// <summary>The edit's fields: how much is left (in the family's unit), the number on the container, the note.</summary>
    public string EditAmount { get; set; } = "";

    public string EditNumber { get; set; } = "";

    public string EditLabel { get; set; } = "";

    private readonly Dictionary<EntryId, MilkBagDetails> _details = [];

    /// <summary>Pumping is a kind the family logs: Stats shows the stash's card.</summary>
    [ObservableProperty]
    private bool _isPumpShown = true;

    public string UnitLabel => Strings.Get(_formats.Unit == VolumeUnit.Ounces ? "Unit.Oz" : "Unit.Ml");

    /// <summary>The last day a date picker may choose: milk is not expressed in the future.</summary>
    public DateTime Today => Local(time.GetUtcNow()).Date;

    public async Task LoadAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        _household = household?.Id;
        _formats = new Formats(clock.Is24Hour, household?.VolumeUnit ?? VolumeUnit.Millilitres);
        OnPropertyChanged(nameof(UnitLabel));
        if (household is null)
        {
            (Bags, Totals, Warning, CanChange, IsReady) = ([], "", "", false, true);
            return;
        }

        if (!IsReady)
            ResetForm();
        IsPumpShown = Coo.Mobile.Presentation.Today.KindVisibility.IsShown(household, "pump");
        CanChange = await access.CanLogAsync(household.Id, CancellationToken.None);
        var stash = await get.ExecuteAsync(household.Id, time.GetUtcNow(), CancellationToken.None);
        _details.Clear();
        foreach (var bag in stash.Bags)
            _details[bag.Entry.Id] = bag.Details;
        Bags = [.. stash.Bags.Select(Row)];
        Totals = TotalsLine(stash, _formats);
        Warning = WarningLine(stash);
        IsReady = true;
    }

    public async Task<bool> AddAsync()
    {
        if (_household is not { } household || !CanChange)
            return false;
        if (!decimal.TryParse(Amount, NumberStyles.Number, Strings.Culture, out var amount) || amount <= 0)
        {
            Error = Strings.Get("Stash.AmountNeeded");
            return false;
        }

        var volume = _formats.Unit == VolumeUnit.Ounces ? Volume.FromOunces(amount) : Volume.FromMillilitres(amount);
        if (volume.IsFailure)
        {
            Error = Strings.Error(volume.Error!.Code);
            return false;
        }

        var local = ExpressedDate.Date + ExpressedTime;
        var expressed = new DateTimeOffset(local, time.LocalTimeZone.GetUtcOffset(local));
        if (expressed > time.GetUtcNow())
            expressed = time.GetUtcNow();
        var result = await add.ExecuteAsync(household, volume.Value, Place, expressed, Label, CancellationToken.None);
        if (result.IsFailure)
        {
            Error = Strings.Error(result.Error!.Code);
            return false;
        }

        ResetForm();
        await LoadAsync();
        return true;
    }

    public async Task ChangeAsync(EntryId bag, MilkBagChange what)
    {
        if (!CanChange)
            return;
        var result = await change.ExecuteAsync(bag, what, time.GetUtcNow(), time.LocalTimeZone, CancellationToken.None);
        Error = result.IsFailure ? Strings.Error(result.Error!.Code) : "";
        await LoadAsync();
    }

    public void ChoosePlace(MilkPlace place) => Place = place;

    /// <summary>Opens a bag's edit with what it holds now.</summary>
    public void StartEdit(EntryId bag)
    {
        if (!CanChange || !_details.TryGetValue(bag, out var details))
            return;
        EditAmount = (_formats.Unit == VolumeUnit.Ounces ? Math.Round(details.Volume.Ounces, 1) : details.Volume.Millilitres).ToString("0.#", Strings.Culture);
        EditNumber = details.Number?.ToString(CultureInfo.InvariantCulture) ?? "";
        EditLabel = details.Label ?? "";
        Error = "";
        Editing = bag;
    }

    public void CancelEdit() => (Editing, Error) = (null, "");

    /// <summary>Saves how much is left, the number and the note; a wrong amount or number says so and keeps the edit open.</summary>
    public async Task<bool> SaveEditAsync()
    {
        if (Editing is not { } bag || !CanChange)
            return false;
        if (!decimal.TryParse(EditAmount, NumberStyles.Number, Strings.Culture, out var amount) || amount <= 0)
        {
            Error = Strings.Get("Stash.AmountNeeded");
            return false;
        }

        int? number = null;
        if (EditNumber.Trim().Length > 0)
        {
            if (!int.TryParse(EditNumber.Trim(), NumberStyles.Integer, Strings.Culture, out var n) || n is < 1 or > MilkBagDetails.MaxNumber)
            {
                Error = Strings.Format("Stash.NumberRange", MilkBagDetails.MaxNumber);
                return false;
            }

            number = n;
        }

        var volume = _formats.Unit == VolumeUnit.Ounces ? Volume.FromOunces(amount) : Volume.FromMillilitres(amount);
        if (volume.IsFailure)
        {
            Error = Strings.Error(volume.Error!.Code);
            return false;
        }

        var result = await change.EditAsync(bag, volume.Value, number, EditLabel, CancellationToken.None);
        if (result.IsFailure)
        {
            Error = Strings.Error(result.Error!.Code);
            return false;
        }

        (Editing, Error) = (null, "");
        await LoadAsync();
        return true;
    }

    /// <summary>"Fridge 210 ml · Freezer 1050 ml", only the places with milk; empty for an empty stash.</summary>
    public static string TotalsLine(MilkStashSummary stash, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(stash);
        ArgumentNullException.ThrowIfNull(formats);
        var parts = new List<string>();
        if (stash.FridgeMl > 0)
            parts.Add($"{Strings.Get("Stash.Fridge")} {Ml(stash.FridgeMl, formats)}");
        if (stash.FreezerMl > 0)
            parts.Add($"{Strings.Get("Stash.Freezer")} {Ml(stash.FreezerMl, formats)}");
        return string.Join(" · ", parts);
    }

    public static string WarningLine(MilkStashSummary stash)
    {
        ArgumentNullException.ThrowIfNull(stash);
        var parts = new List<string>();
        if (stash.UseSoon > 0)
            parts.Add(Strings.Plural("Stash.UseSoon", stash.UseSoon));
        if (stash.Expired > 0)
            parts.Add(Strings.Plural("Stash.Expired", stash.Expired));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Today's line, only when there is something to do: "Milk stash · 1 bag to use soon". A stash that is all fine stays
    /// off Today; Stats shows it.
    /// </summary>
    public static string? TodayLine(MilkStashSummary? stash)
    {
        if (stash is null || stash.IsEmpty)
            return null;
        var warning = WarningLine(stash);
        return warning.Length > 0 ? $"{Strings.Get("Kind.MilkBag")} · {warning}" : null;
    }

    /// <summary>A total that may pass one bag's limit: 1,240 ml or 41.9 oz.</summary>
    public static string Ml(decimal millilitres, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        return formats.Unit == VolumeUnit.Ounces
            ? $"{Math.Round(millilitres / Coo.Domain.CareLog.Volume.MillilitresPerUsFluidOunce, 1).ToString("0.#", Strings.Culture)} {Strings.Get("Unit.Oz")}"
            : $"{Math.Round(millilitres).ToString("#,0", Strings.Culture)} {Strings.Get("Unit.Ml")}";
    }

    private MilkBagRow Row(StashBag bag)
    {
        var where = bag.Details.ThawedAt is not null ? Strings.Get("Stash.Thawed")
            : bag.Details.Place == MilkPlace.Freezer ? Strings.Get("Stash.Freezer") : Strings.Get("Stash.Fridge");
        var dates = Strings.Format("Stash.Dates", When(bag.ExpressedAt), When(bag.UseBy));
        return new MilkBagRow(
            bag.Entry.Id, _formats.Volume(bag.Details.Volume), where, dates, bag.Freshness, bag.Details.Place, bag.Details.ThawedAt is not null,
            bag.Details.Label, bag.Details.Number is { } n ? $"#{n}" : "");
    }

    /// <summary>"Mon 3 Nov 08:30" in this phone's zone.</summary>
    private string When(DateTimeOffset at) =>
        $"{Local(at).ToString("ddd d MMM", Strings.Culture)} {_formats.Time(at, time.LocalTimeZone)}";

    private void ResetForm()
    {
        var now = Local(time.GetUtcNow());
        (Amount, Label, Error, Place, ExpressedDate, ExpressedTime) = ("", "", "", MilkPlace.Fridge, now.Date, new TimeSpan(now.Hour, now.Minute, 0));
    }

    private DateTime Local(DateTimeOffset at) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(at, time.LocalTimeZone).DateTime, DateTimeKind.Unspecified);
}
