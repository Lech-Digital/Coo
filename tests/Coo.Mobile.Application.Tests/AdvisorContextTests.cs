using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.CareLog;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class AdvisorContextTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(2));
    private long _wall = 1;

    [Fact]
    public async Task ExecuteAsync_NapsLoggedToday_TellsTheAdvisorAboutThem()
    {
        // Arrange: the owner's finding — two naps today, and the Advisor said nothing had been logged for days.
        var context = Context([
            Sleep(At(22, 11, 45), At(22, 11, 50)),
            Sleep(At(22, 11, 53), At(22, 13, 17)),
            Diaper(At(22, 10, 52), DiaperContents.Wet),
        ]);

        // Act
        var text = await context.ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("Thu 22 Oct (today so far): sleep 1 h 29 m in 2 sleeps");
        text.Should().Contain("Awake since Thu 13:17");
        text.Should().Contain("diapers 1 (1 wet, 0 dirty)");
    }

    [Fact]
    public async Task ExecuteAsync_SleepRunning_SaysSoInsteadOfAwake()
    {
        // Arrange
        var running = Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(At(22, 13, 30), null).Value));
        var context = Context([running]);

        // Act
        var text = await context.ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("In progress: sleep of 30 m since Thu 13:30");
        text.Should().NotContain("Awake since");
    }

    [Fact]
    public async Task ExecuteAsync_NothingLogged_SaysSoForEachDay()
    {
        // Act
        var text = await Context([]).ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("nothing logged");
        text.Should().Contain("This week so far: nothing logged.");
    }

    [Fact]
    public async Task ExecuteAsync_FeverMedicineAndWeighIn_AreToldWithTheWhoPercentile()
    {
        // Arrange: born 20 August; 5.1 kg at two months is about the WHO median for a girl.
        var store = Store([
            Make(Coo.Domain.CareLog.Kinds.Health.TemperatureDetails.Create(38.6m).Value, EntryTiming.Point(At(21, 20, 0))),
            Make(Coo.Domain.CareLog.Kinds.Health.MedicineDetails.Create("Paracetamol", "2.5 ml").Value, EntryTiming.Point(At(21, 20, 5))),
            Make(Coo.Domain.CareLog.Kinds.Growth.GrowthDetails.Create(5_100, null, null).Value, EntryTiming.Point(At(20, 10, 0))),
        ]);
        var households = Substitute.For<Coo.Mobile.Application.Households.IHouseholdStore>();
        households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns(
            [Coo.Domain.Households.BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 22)).Value]);
        var chart = Substitute.For<Coo.Mobile.Application.Growth.IGrowthChartPreference>();
        chart.GetAsync(Arg.Any<CancellationToken>()).Returns(Coo.Mobile.Application.Growth.ChartSex.Girl);

        // Act
        var text = await new BuildAdvisorContext(new GetToday(store), new GetCareTrends(store), store, households, chart)
            .ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("Health, last 14 days:");
        text.Should().Contain("temperature 38.6 °C (fever)");
        text.Should().Contain("medicine Paracetamol, 2.5 ml");
        text.Should().Contain("weight 5.1 kg (P48)");
    }

    [Fact]
    public async Task ExecuteAsync_NoHealthOrGrowth_AddsNoSections()
    {
        // Arrange
        var store = Store([]);

        // Act
        var text = await new BuildAdvisorContext(new GetToday(store), new GetCareTrends(store), store)
            .ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().NotContain("Health, last 14 days").And.NotContain("Growth measurements");
    }

    [Fact]
    public async Task ExecuteAsync_EntriesOfTheLastTwoDays_AreListedOneByOneWithTheirTimes()
    {
        // Arrange: a night sleep yesterday, a diaper this morning, and a diaper three days ago that is only counted
        var store = Store([Sleep(At(21, 19, 30), At(22, 6, 15)), Diaper(At(22, 9, 5), DiaperContents.Dirty), Diaper(At(19, 9, 0), DiaperContents.Wet)]);

        // Act
        var text = await new BuildAdvisorContext(new GetToday(store), new GetCareTrends(store), store)
            .ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("Every entry of the last 48 hours");
        text.Should().Contain("- Wed 19:30–06:15 sleep of 10 h 45 m");
        text.Should().Contain("- Thu 09:05 diaper, dirty");
        text.Should().NotContain("Mon 09:00 diaper", "older entries are in the daily totals, not one by one");
        text.Should().Contain("Per day, last 14 days");
    }

    [Fact]
    public async Task ExecuteAsync_AWeekOfNights_TellsWhatMochiLearned()
    {
        // Arrange: nights 19:30–06:00 all week, a nap at 10:00 each day.
        var entries = new List<CareEntry>();
        for (var day = 14; day <= 21; day++)
        {
            entries.Add(Sleep(At(day, 10, 0), At(day, 11, 0)));
            entries.Add(Sleep(At(day, 19, 30), At(day + 1, 6, 0)));
        }

        entries.Add(Sleep(At(22, 10, 0), At(22, 11, 0)));
        var store = Store(entries);
        var households = Substitute.For<Coo.Mobile.Application.Households.IHouseholdStore>();
        households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns(
            [Coo.Domain.Households.BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 6, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 22)).Value]);

        // Act
        var text = await new BuildAdvisorContext(
                new GetToday(store), new GetCareTrends(store), store, households, sleep: new Insights.GetSleepPrediction(store),
                pattern: new Insights.GetSleepPattern(store))
            .ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("This baby's night has usually started around 19:30");
        text.Should().Contain("Longest sleep of the day, last 14 days: usually starts around 19:30 and lasts about 10 h 30 m.");
    }

    [Fact]
    public async Task ExecuteAsync_TwoDaysLogged_TellsNoPattern()
    {
        // Arrange: too little to learn from.
        var store = Store([Sleep(At(20, 19, 30), At(21, 6, 0)), Sleep(At(21, 19, 30), At(22, 6, 0))]);
        var households = Substitute.For<Coo.Mobile.Application.Households.IHouseholdStore>();
        households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns(
            [Coo.Domain.Households.BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 6, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 22)).Value]);

        // Act
        var text = await new BuildAdvisorContext(
                new GetToday(store), new GetCareTrends(store), store, households, pattern: new Insights.GetSleepPattern(store))
            .ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().NotContain("Longest sleep of the day");
    }

    private static ICareLogStore Store(IReadOnlyList<CareEntry> entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default).ReturnsForAnyArgs(_ => entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());
        return store;
    }

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(2));

    private static BuildAdvisorContext Context(IReadOnlyList<CareEntry> entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default).ReturnsForAnyArgs(_ => entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());
        return new BuildAdvisorContext(new GetToday(store), new GetCareTrends(store));
    }

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset end) =>
        Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Diaper(DateTimeOffset at, DiaperContents contents) => Make(new DiaperDetails(contents), EntryTiming.Point(at));

    private CareEntry Make(EntryDetails details, EntryTiming timing) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))),
            Household,
            EntrySubject.Baby(Baby),
            details,
            timing,
            PersonRef.ThisDevicesPerson,
            EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device),
            Now).Value;
}
