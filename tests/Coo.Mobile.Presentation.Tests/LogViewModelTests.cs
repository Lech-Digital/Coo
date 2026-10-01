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

    private static LogViewModel Model()
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
        var time = new FixedTime(Now);
        var versions = Substitute.For<IVersionClock>();
        var undo = new UndoService(new DeleteEntry(store, versions), new ReviseEntry(store, versions, time), store, time);
        return new LogViewModel(households, new GetDay(store), clock, undo, time);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
