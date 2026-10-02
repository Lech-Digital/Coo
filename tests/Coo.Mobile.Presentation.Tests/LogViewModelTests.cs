using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Today;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

public sealed class LogViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    [Fact]
    public async Task RefreshAsync_NothingChanged_RaisesNoPropertyChanged()
    {
        // Arrange: owner's finding on the Pixel 10 — every tab visit redrew Log, even with the same data.
        var model = Model();
        await model.RefreshAsync();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        await model.RefreshAsync();

        // Assert
        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_FirstTime_RaisesRowsAndTotals()
    {
        // Arrange
        var model = Model();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        await model.RefreshAsync();

        // Assert
        changed.Should().Contain([nameof(LogViewModel.Totals), nameof(LogViewModel.Title)]);
    }

    [Fact]
    public async Task RefreshAsync_Today_PutsNowAt14()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.NowAt.Should().BeApproximately(14.0 / 24, 1e-9);
    }

    [Fact]
    public async Task RefreshAsync_PastDay_HasNoNow()
    {
        // Arrange
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.PreviousCommand.ExecuteAsync(null);

        // Assert
        model.NowAt.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_DefaultQuietHours_ShadesBothEndsOfTheDay()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.QuietBands.Should().HaveCount(2);
        model.Hours.Should().HaveCount(25);
    }

    [Fact]
    public async Task RefreshAsync_TwentySecondsLater_RaisesNothing()
    {
        // Arrange: the owner's Pixel 10 finding — a return to Log must not redraw it.
        var time = new MovableTime(Now);
        var model = Model(time);
        await model.RefreshAsync();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        time.Now = Now.AddSeconds(20);

        // Act
        await model.RefreshAsync();

        // Assert
        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task RangeTitle_AfterRefresh_NamesToday()
    {
        // Arrange
        var model = Model();
        await model.RefreshAsync();

        // Act
        var title = model.RangeTitle(12.5 / 24, 18.5 / 24);

        // Assert
        title.Should().Contain("12:30").And.Contain("18:30");
    }

    private static LogViewModel Model(TimeProvider? timeProvider = null)
    {
        var households = Substitute.For<IHouseholdStore>();
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([baby]);
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default).ReturnsForAnyArgs(Array.Empty<StoredEntry>());
        var clock = Substitute.For<IClockFormat>();
        clock.Is24Hour.Returns(true);
        var time = timeProvider ?? new MovableTime(Now);
        var versions = Substitute.For<IVersionClock>();
        var undo = new UndoService(new DeleteEntry(store, versions), new ReviseEntry(store, versions, time), store, time);
        var theme = new Coo.Mobile.Presentation.Theme.ThemeController(
            Substitute.For<IDevicePreferences>(), Substitute.For<Coo.Mobile.Presentation.Theme.IOsAppearance>(), time);
        return new LogViewModel(households, new GetDay(store), clock, undo, theme, time);
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
