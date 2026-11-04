using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.MilkStash;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.MilkStash;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The milk stash in words: totals, warnings, Today's line, in every language.</summary>
[Collection(nameof(StringsState))]
public sealed class MilkStashTextTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly Formats Ml = new(true, VolumeUnit.Millilitres);
    private static readonly int[] Counts = [1, 2, 5];
    private int _n;

    public MilkStashTextTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    public static TheoryData<string> Languages => ["en", "de", "fr", "es", "it", "pl", "nl"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Keys_EveryStashText_HasItsWordsAndPlurals(string language)
    {
        // Arrange
        Strings.Use(new LanguagePreference(language), null);
        string[] keys = ["Kind.MilkBag", "Entry.MilkBagFridge", "Entry.MilkBagFreezer", "Entry.MilkBagUsed", "Entry.MilkBagDiscarded",
            "Stash.Fridge", "Stash.Freezer", "Stash.Thawed", "Stash.Dates", "Stash.Empty", "Stash.Add", "Stash.Amount", "Stash.Expressed",
            "Stash.LabelHint", "Stash.AmountNeeded", "Stash.Use", "Stash.Freeze", "Stash.Thaw", "Stash.Discard", "Stash.UseFirst",
            "Stash.ExpiredTag", "Stash.SoonTag", "Stash.Guidance", "Stash.Open", "Stash.Hint", "Stash.Edit", "Stash.Save", "Stash.Left",
            "Stash.Number", "Stash.NumberRange"];

        // Act
        var missing = keys.Where(k => Strings.Get(k) == k).ToList();
        var plurals = Counts.Select(n => Strings.Plural("Stash.UseSoon", n) + Strings.Plural("Stash.Expired", n)).ToList();

        // Assert
        missing.Should().BeEmpty();
        plurals.Should().OnlyContain(p => !p.Contains("Stash.", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_AStashWithAnOldFridgeBag_TotalsWarnsAndShowsOnToday()
    {
        // Arrange
        var stash = new MilkStashSummary([
            Bag(90, MilkPlace.Fridge, Now.AddDays(-3.5), MilkFreshness.UseSoon),
            Bag(120, MilkPlace.Fridge, Now.AddHours(-2), MilkFreshness.Fine),
            Bag(525, MilkPlace.Freezer, Now.AddDays(-20), MilkFreshness.Fine),
            Bag(525, MilkPlace.Freezer, Now.AddDays(-19), MilkFreshness.Fine)]);
        var fine = new MilkStashSummary([Bag(120, MilkPlace.Fridge, Now.AddHours(-2), MilkFreshness.Fine)]);

        // Act
        var totals = MilkStashViewModel.TotalsLine(stash, Ml);
        var today = MilkStashViewModel.TodayLine(stash);
        var quiet = MilkStashViewModel.TodayLine(fine);

        // Assert
        totals.Should().Be("Fridge 210 ml · Freezer 1,050 ml");
        today.Should().Be("Milk stash · 1 bag to use soon");
        quiet.Should().BeNull("a stash that is all fine stays off Today");
        MilkStashViewModel.TodayLine(MilkStashSummary.Empty).Should().BeNull();
    }

    private StashBag Bag(decimal ml, MilkPlace place, DateTimeOffset expressed, MilkFreshness freshness)
    {
        var details = MilkBagDetails.Create(Volume.FromMillilitres(ml).Value, place).Value;
        var entry = CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_n))), Household, EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), details,
            EntryTiming.Point(expressed), PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(_n, 0, Device), Now).Value;
        return new StashBag(entry, details, MilkStorage.UseBy(details, expressed), freshness);
    }
}
