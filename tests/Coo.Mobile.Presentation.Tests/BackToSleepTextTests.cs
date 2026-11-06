using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>"Back to sleep" at night, in words: a time range, after the feed, or her usual once past it.</summary>
[Collection(nameof(StringsState))]
public sealed class BackToSleepTextTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 3, 10, 0, TimeSpan.Zero);

    public BackToSleepTextTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static HomeSnapshot Snapshot(NightReturn back, DateTimeOffset now)
    {
        var household = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
        var stamp = new Coo.Domain.Common.Versioning.HlcStamp(1, 0, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")));
        var baby = BabyProfile.Create(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), household, "Lena", [], new DateOnly(2026, 9, 17), null,
            stamp, new DateOnly(2026, 11, 6)).Value;
        var settings = HouseholdSettings.Create(household, VolumeUnit.Millilitres, "Europe/Amsterdam", stamp).Value;
        return new HomeSnapshot(settings, baby, null, now, Theme.PaletteKind.Night, new Formats(true, VolumeUnit.Millilitres), BackToSleep: back);
    }

    private static NightReturn Back(DateTimeOffset? earliest, DateTimeOffset? latest, bool feedRunning = false) =>
        new(Now.AddMinutes(-10), earliest, latest, TimeSpan.FromMinutes(25), feedRunning, feedRunning, 6, 4, NightWakingPredictor.Version);

    [Fact]
    public void BackToSleep_BeforeTheWindowEnds_ShowsTheRange()
    {
        // Arrange
        var back = Back(Now.AddMinutes(15), Now.AddMinutes(35));

        var formats = new Formats(true, VolumeUnit.Millilitres);

        // Act
        var text = NextUpSection.BackToSleep(back, Snapshot(back, Now));

        // Assert
        text.Should().Be($"likely {formats.Time(Now.AddMinutes(15))}–{formats.Time(Now.AddMinutes(35))}");
    }

    [Fact]
    public void BackToSleep_FeedRunning_SaysAfterTheFeed()
    {
        // Arrange
        var back = Back(null, null, feedRunning: true);

        // Act
        var text = NextUpSection.BackToSleep(back, Snapshot(back, Now));

        // Assert
        text.Should().Be("about 25 m after the feed");
    }

    [Fact]
    public void BackToSleep_PastTheWindow_GivesHerUsualInsteadOfATimeGoneBy()
    {
        // Arrange
        var back = Back(Now.AddMinutes(-20), Now.AddMinutes(-5));

        // Act
        var text = NextUpSection.BackToSleep(back, Snapshot(back, Now));

        // Assert
        text.Should().Be("usually within 25 m");
    }

    private static FeedPrediction Feed(DateTimeOffset earliest, DateTimeOffset latest, bool cluster = false) =>
        new(Now.AddHours(-2), earliest, latest, TimeSpan.FromHours(3), cluster, 20, 7, FeedPredictor.Version);

    [Fact]
    public void NextFeedText_BeforeTheWindow_ShowsTheRange()
    {
        // Arrange
        var next = Feed(Now.AddMinutes(30), Now.AddMinutes(70));
        var formats = new Formats(true, VolumeUnit.Millilitres);

        // Act
        var text = NextUpSection.NextFeedText(next, Snapshot(Back(null, null), Now), "Lena", shortForm: false);

        // Assert
        text.Should().Be($"Next feed likely {formats.Time(Now.AddMinutes(30))}–{formats.Time(Now.AddMinutes(70))}");
    }

    [Fact]
    public void NextFeedText_ClusterTime_SaysFeedsComeCloseTogether()
    {
        // Arrange
        var next = Feed(Now.AddMinutes(30), Now.AddMinutes(70), cluster: true);

        // Act
        var text = NextUpSection.NextFeedText(next, Snapshot(Back(null, null), Now), "Lena", shortForm: false);

        // Assert
        text.Should().Be("Cluster-feeding time for Lena: feeds may come close together");
    }

    [Fact]
    public void NextFeedText_PastTheWindow_GivesHerUsualInterval()
    {
        // Arrange
        var next = Feed(Now.AddMinutes(-60), Now.AddMinutes(-20));

        // Act
        var text = NextUpSection.NextFeedText(next, Snapshot(Back(null, null), Now), "Lena", shortForm: true);

        // Assert
        text.Should().Be($"usually every {Formats.Duration(TimeSpan.FromHours(3))}");
    }
}
