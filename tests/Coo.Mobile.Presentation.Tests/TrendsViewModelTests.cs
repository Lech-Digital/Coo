using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

[Collection(nameof(StringsState))]
public sealed class TrendsViewModelTests : IDisposable
{
    // Thursday 22 October 2026, 14:00 in Amsterdam.
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private readonly List<CareEntry> _entries = [];
    private long _wall = 1;

    public TrendsViewModelTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public async Task RefreshAsync_Weeks_SelectsThisWeekAndComparesItWithTheWeekBefore()
    {
        // Arrange: 12 h a day last week, 13 h a day so far this week.
        for (var d = 12; d <= 18; d++)
            _entries.Add(Sleep(At(d, 8), At(d, 20)));
        for (var d = 19; d <= 21; d++)
            _entries.Add(Sleep(At(d, 7), At(d, 20)));
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.Bars.Should().HaveCount(TrendsViewModel.BarsFor(TrendGrain.Week));
        model.Selected.Should().Be(model.Bars.Count - 1);
        model.Value.Should().Be("13 h 0 m");
        model.Change.Should().Be("+1 h 0 m vs the week before");
        model.PeriodTitle.Should().Be("19 Oct – 25 Oct");
        model.PeriodCaption.Should().Be("3 days logged · so far");
        model.CanGoNext.Should().BeFalse();
    }

    [Fact]
    public async Task SelectCommand_LastWeeksBar_ComparesLastWeekWithTheOneBeforeIt()
    {
        // Arrange: nothing two weeks ago, so last week has nothing to compare with.
        for (var d = 12; d <= 18; d++)
            _entries.Add(Sleep(At(d, 8), At(d, 20)));
        var model = Model();
        await model.RefreshAsync();

        // Act
        model.SelectCommand.Execute(model.Bars.Count - 2);

        // Assert
        model.Value.Should().Be("12 h 0 m");
        model.Change.Should().Be("Nothing logged before to compare with");
        model.Bars[^2].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task SetMetricCommand_Diapers_ChartsDiapersAndKeepsEveryRow()
    {
        // Arrange
        _entries.Add(Diaper(At(21, 9), DiaperContents.Mixed));
        _entries.Add(Diaper(At(21, 12), DiaperContents.Wet));
        var model = Model();
        await model.RefreshAsync();

        // Act
        model.SetMetricCommand.Execute(TrendMetric.Diapers);

        // Assert
        model.MetricName.Should().Be("Diapers per day");
        model.KindCode.Should().Be("diaper");
        model.Bars[^1].Value.Should().Be(2);
        model.Rows.Single(r => r.IsSelected).Metric.Should().Be(TrendMetric.Diapers);
        model.Rows.Single(r => r.Metric == TrendMetric.Diapers).Detail.Should().Be("2 wet · 1 dirty");
    }

    [Fact]
    public async Task SetGrainCommand_Days_ShowsTodayAsSoFar()
    {
        // Arrange
        _entries.Add(Diaper(At(22, 9), DiaperContents.Wet));
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.SetGrainCommand.ExecuteAsync(TrendGrain.Day);

        // Assert
        model.Bars.Should().HaveCount(TrendsViewModel.BarsFor(TrendGrain.Day));
        model.Bars[^1].IsPartial.Should().BeTrue();
        model.Selected.Should().Be(model.Bars.Count - 1, "today is the only day with data");
        model.PeriodTitle.Should().Be("Today");
        model.PeriodCaption.Should().Be("so far");
    }

    [Fact]
    public async Task SetGrainCommand_DaysWithYesterdayLogged_SelectsYesterdayNotTodaySoFar()
    {
        // Arrange
        _entries.Add(Sleep(At(21, 8), At(21, 20)));
        _entries.Add(Sleep(At(22, 9), At(22, 11)));
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.SetGrainCommand.ExecuteAsync(TrendGrain.Day);

        // Assert
        model.Selected.Should().Be(model.Bars.Count - 2);
        model.Value.Should().Be("12 h 0 m");
    }

    [Fact]
    public async Task PreviousCommand_Weeks_ShowsTheEightWeeksBeforeAndAllowsNext()
    {
        // Arrange
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.PreviousCommand.ExecuteAsync(null);

        // Assert
        model.CanGoNext.Should().BeTrue();
        model.PeriodTitle.Should().Be("24 Aug – 30 Aug");
        model.Value.Should().Be("Nothing logged");
    }

    private TrendsViewModel Model()
    {
        var households = Substitute.For<IHouseholdStore>();
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([baby]);
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default).ReturnsForAnyArgs(_ => _entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2"));
        return new TrendsViewModel(households, new GetCareTrends(store), time);
    }

    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(2));

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset end) =>
        Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Diaper(DateTimeOffset at, DiaperContents contents) =>
        Make(new DiaperDetails(contents), EntryTiming.Point(at));

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
