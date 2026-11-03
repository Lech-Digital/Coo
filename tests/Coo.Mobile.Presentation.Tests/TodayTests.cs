using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

public sealed class TodayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 3, 12, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1;

    public TodayTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task StartBreastAsync_RightSide_SavesAnOpenFeedOnTheRight()
    {
        // Arrange
        var actions = Actions();
        CareEntry? saved = null;
        await _store.SaveAsync(Arg.Do<CareEntry>(e => saved = e), Arg.Any<CancellationToken>());

        // Act
        await actions.StartBreastAsync(BreastSide.Right);

        // Assert
        var feed = saved!.Details.Should().BeOfType<BreastFeed>().Subject;
        feed.LastSide.Should().Be(BreastSide.Right);
        saved.Timing.IsInProgress.Should().BeTrue();
        saved.Source.Should().Be(EntrySource.Timer);
    }

    [Fact]
    public void Update_NightWithRunningFeed_ShowsTimerFactsAndQuickRow()
    {
        // Arrange
        var running = Entry(BreastFeed.Start(BreastSide.Right, Now.AddMinutes(-8)));
        var section = new NightSection(Actions());
        var summary = new TodaySummary([running], running, null, null, null, Now.AddHours(-1), [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));

        // Act
        section.Update(Snapshot(summary, PaletteKind.Night));

        // Assert
        section.Timers.Should().ContainSingle().Which.Digits.Should().Be("08:00");
        section.Facts.Should().HaveCount(4);
        section.Quick.Select(q => q.KindCode).Should().Contain(["diaper", "sleep", "feeding", "pump"]);
        section.AppliesTo(HomeStage.Born, PaletteKind.Night).Should().BeTrue();
        section.AppliesTo(HomeStage.Born, PaletteKind.Day).Should().BeFalse();
    }

    [Fact]
    public void Update_ExpectedBabyDueInSixWeeks_ShowsTheCountdown()
    {
        // Arrange
        var expected = BabyProfile.Create(Baby, Household, "Baby", [], null, new DateOnly(2026, 11, 12), new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var section = new DueDateSection(Actions());

        // Act
        section.Update(new HomeSnapshot(Settings(), expected, null, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres)));

        // Assert
        section.Headline.Should().Be("Due in 6 weeks");
        section.AppliesTo(HomeStage.Expecting, PaletteKind.Night).Should().BeTrue();
        section.AppliesTo(HomeStage.Born, PaletteKind.Day).Should().BeFalse();
    }

    [Fact]
    public void Duration_MinutesAndHours_ReadAsDesigned()
    {
        // Arrange
        var short1 = TimeSpan.FromMinutes(45);
        var long1 = TimeSpan.FromMinutes(80);

        // Act
        var (a, b) = (Formats.Duration(short1), Formats.Duration(long1));

        // Assert
        (a, b).Should().Be(("45 m", "1 h 20 m"));
    }

    [Fact]
    public void Volume_OuncesHousehold_ShowsOunces()
    {
        // Arrange
        var formats = new Formats(true, VolumeUnit.Ounces);

        // Act
        var text = formats.Volume(Volume.FromOunces(4).Value);

        // Assert
        text.Should().Be("4 oz");
    }

    private HomeActions Actions()
    {
        var time = new FixedTime(Now);
        return new HomeActions(new LogEntry(_store, _clock, time), new ReviseEntry(_store, _clock, time), time)
        {
            Context = new HomeContext(Household, Baby),
        };
    }

    private CareEntry Entry(EntryDetails details) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Baby(Baby), details, null,
            PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(++_wall, 0, Device), Now).Value;

    private static HouseholdSettings Settings() =>
        HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;

    private static HomeSnapshot Snapshot(TodaySummary summary, PaletteKind palette)
    {
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        return new HomeSnapshot(Settings(), baby, summary, Now, palette, new Formats(true, VolumeUnit.Millilitres));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}

public sealed class AwakeCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 3, 12, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly List<CareEntry> _saved = [];
    private long _wall = 1;

    public AwakeCheckTests()
    {
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        _store.SaveAsync(Arg.Do<CareEntry>(e => _saved.Add(e)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogDiaperAsync_WhileAsleep_AsksFirstAndLogsNothing()
    {
        // Arrange
        var actions = Actions(asleep: Sleep());
        AwakeCheck? asked = null;
        actions.AwakeCheckRequested += (_, check) => asked = check;

        // Act
        await actions.LogDiaperAsync(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Wet);

        // Assert
        asked.Should().NotBeNull();
        _saved.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAwakeCheckAsync_WokeUp_EndsTheSleepThenLogs()
    {
        // Arrange
        var sleep = Sleep();
        var actions = Actions(asleep: sleep);
        _store.FindAsync(sleep.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(sleep));
        AwakeCheck? asked = null;
        actions.AwakeCheckRequested += (_, check) => asked = check;
        await actions.LogDiaperAsync(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Wet);

        // Act
        await actions.ResolveAwakeCheckAsync(asked!, wokeUp: true);

        // Assert
        sleep.Timing.IsInProgress.Should().BeFalse("woke up ends the sleep now");
        _saved.Select(e => e.KindCode.Value).Should().Equal("sleep", "diaper");
    }

    [Fact]
    public async Task ResolveAwakeCheckAsync_StillAsleep_LogsAndKeepsTheSleepRunning()
    {
        // Arrange: a dream feed.
        var sleep = Sleep();
        var actions = Actions(asleep: sleep);
        AwakeCheck? asked = null;
        actions.AwakeCheckRequested += (_, check) => asked = check;
        await actions.StartBreastAsync(BreastSide.Left);

        // Act
        await actions.ResolveAwakeCheckAsync(asked!, wokeUp: false);

        // Assert
        sleep.Timing.IsInProgress.Should().BeTrue();
        _saved.Should().ContainSingle(e => e.Details is BreastFeed);
    }

    [Fact]
    public async Task StartPumpAsync_WhileAsleep_DoesNotAsk()
    {
        // Arrange: pumping is about the parent, and runs alongside a sleep.
        var actions = Actions(asleep: Sleep());
        var asked = false;
        actions.AwakeCheckRequested += (_, _) => asked = true;

        // Act
        await actions.StartPumpAsync();

        // Assert
        asked.Should().BeFalse();
        _saved.Should().ContainSingle();
    }

    private HomeActions Actions(CareEntry? asleep)
    {
        var time = new FixedTime(Now);
        return new HomeActions(new LogEntry(_store, _clock, time), new ReviseEntry(_store, _clock, time), time)
        {
            Context = new HomeContext(Household, Baby),
            RunningSleep = () => asleep is null ? null : (asleep.Id, asleep.Timing.OccurredAt),
        };
    }

    private CareEntry Sleep() =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Baby(Baby), new Coo.Domain.CareLog.Kinds.Sleep.SleepDetails(),
            EntryTiming.Session(TimeRange.Open(Now.AddHours(-3))), PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(++_wall, 0, Device), Now).Value;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}

public sealed class NightTimersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 3, 12, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private long _wall = 1;

    [Fact]
    public void Update_DreamFeedDuringSleep_ShowsBothTimersFeedFirst()
    {
        // Arrange: the sleep started first, then a dream feed.
        var sleep = Entry(new Coo.Domain.CareLog.Kinds.Sleep.SleepDetails(), EntryTiming.Session(TimeRange.Open(Now.AddHours(-2))));
        var feed = Entry(BreastFeed.Start(BreastSide.Left, Now.AddMinutes(-5)), null);
        var summary = new TodaySummary([sleep, feed], feed, null, sleep, null, null, [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));
        var time = new FixedTime(Now);
        var store = Substitute.For<ICareLogStore>();
        var clock = Substitute.For<IVersionClock>();
        var section = new NightSection(new HomeActions(new LogEntry(store, clock, time), new ReviseEntry(store, clock, time), time));
        var baby = BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;

        // Act
        section.Update(new HomeSnapshot(settings, baby, summary, Now, PaletteKind.Night, new Formats(true, VolumeUnit.Millilitres)));

        // Assert
        section.Timers.Select(t => t.KindCode).Should().Equal("feeding", "sleep");
        section.Timers[0].Actions.Should().HaveCount(3, "the feed has Pause, Switch side and Stop at night");
    }

    [Fact]
    public void Update_RunningStartMovedBack_KeepsTheCardAndUpdatesItsStart()
    {
        // Arrange: found on the phone; the card kept "started 14:35" after the start was moved to 14:25.
        var sleep = Entry(new Coo.Domain.CareLog.Kinds.Sleep.SleepDetails(), EntryTiming.Session(TimeRange.Open(Now.AddMinutes(-2))));
        var time = new FixedTime(Now);
        var store = Substitute.For<ICareLogStore>();
        var clock = Substitute.For<IVersionClock>();
        var section = new RunningSection(new HomeActions(new LogEntry(store, clock, time), new ReviseEntry(store, clock, time), time));
        var baby = BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        HomeSnapshot Snapshot(CareEntry running) => new(settings, baby,
            new TodaySummary([running], null, null, running, null, null, [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc)),
            Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres));
        section.Update(Snapshot(sleep));
        var card = section.Items[0];
        var before = card.Started;

        // Act
        sleep.Retime(EntryTiming.Session(TimeRange.Open(Now.AddMinutes(-12))), new HlcStamp(++_wall, 0, Device), Now);
        section.Update(Snapshot(sleep));

        // Assert
        section.Items[0].Should().BeSameAs(card, "the card stays put, so taps on its buttons are never lost");
        card.Started.Should().NotBe(before).And.Contain(new Formats(true, VolumeUnit.Millilitres).Time(Now.AddMinutes(-12)));
    }

    [Fact]
    public void Update_FeedingPaused_UpdatesButtonsToResumeAndShowsPaused()
    {
        // Arrange
        var feed = Entry(BreastFeed.Start(BreastSide.Left, Now.AddMinutes(-10)), null);
        var time = new FixedTime(Now);
        var store = Substitute.For<ICareLogStore>();
        var clock = Substitute.For<IVersionClock>();
        var section = new RunningSection(new HomeActions(new LogEntry(store, clock, time), new ReviseEntry(store, clock, time), time));
        var baby = BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        HomeSnapshot Snapshot(CareEntry running) => new(settings, baby,
            new TodaySummary([running], null, null, running, null, null, [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc)),
            Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres));

        section.Update(Snapshot(feed));
        var card = section.Items[0];
        card.IsPaused.Should().BeFalse();
        card.ActiveSide.Should().Be(BreastSide.Left);
        card.Actions.Select(a => a.Label).Should().Equal("Pause", "Switch side", "Stop");

        // Act: pause the feed
        feed.Revise<BreastFeed>(b => b.Pause(Now.AddMinutes(-2)), new HlcStamp(++_wall, 0, Device), Now);
        section.Update(Snapshot(feed));

        // Assert
        card.IsPaused.Should().BeTrue();
        card.ActiveSide.Should().BeNull();
        card.Actions.Select(a => a.Label).Should().Equal("Resume", "Switch & resume", "Stop");
        card.Actions[0].IsPrimary.Should().BeTrue("Resume is the primary action when paused");
    }

    [Fact]
    public void NextUpSection_NoPrediction_ShowsTheTypicalWakeWindowWithoutAPremiumNudge()
    {
        // Arrange
        var section = new NextUpSection();

        // Act
        section.Update(NextUpSnapshot(isPremium: false, sleep: null));

        // Assert
        section.HasContent.Should().BeTrue();
        section.NextSide.Should().Contain("right", "alternates from previous left side");
        section.SinceFeed.Should().Contain("45 m");
        section.NextNap.Should().Contain("60–90 min");
        section.NapUpsell.Should().BeFalse();
        section.NapIsPersonal.Should().BeFalse();
    }

    [Fact]
    public void NextUpSection_WithoutPremium_ShowsThePersonalWindowToo()
    {
        // Arrange: personal nap times are free (owner, 2026-11-03).
        var section = new NextUpSection();

        // Act
        section.Update(NextUpSnapshot(isPremium: false, Prediction(PredictionConfidence.High)));

        // Assert
        section.NapIsPersonal.Should().BeTrue();
        section.NextNap.Should().StartWith("Nap likely");
    }

    [Fact]
    public void NextUpSection_PremiumHighConfidence_ShowsThePersonalWindowAndWhy()
    {
        // Arrange
        var section = new NextUpSection();
        var sleep = Prediction(PredictionConfidence.High);

        // Act
        section.Update(NextUpSnapshot(isPremium: true, sleep));

        // Assert
        section.NapIsPersonal.Should().BeTrue();
        // In the phone's zone, as Today shows it: CI runs in UTC, the developer's Mac in Amsterdam.
        section.NextNap.Should().Be($"Nap likely {Now.AddMinutes(40).ToLocalTime():HH:mm}–{Now.AddMinutes(70).ToLocalTime():HH:mm}");
        section.NapWhy.Should().Contain(line => line.Contains("Lena") && line.Contains("1 h 20 m"));
        section.NapWhy[^1].Should().Contain("not medical advice");
    }

    [Fact]
    public void NextUpSection_LowConfidenceWithWeeksOfWindows_StillShowsHerOwnWindow()
    {
        // Arrange: a young baby's windows vary by more than the High spread; three weeks of them are still hers (owner,
        // 2026-11-03: "it still says log a few more days").
        var section = new NextUpSection();

        // Act
        section.Update(NextUpSnapshot(isPremium: false, Prediction(PredictionConfidence.Low)));

        // Assert
        section.NapIsPersonal.Should().BeTrue();
        section.NextNap.Should().StartWith("Nap likely");
    }

    [Fact]
    public void NextUpSection_TooFewOwnWindows_ShowsTheTypicalWindowAndAsksForMoreDays()
    {
        // Arrange: two windows, on two days.
        var section = new NextUpSection();
        var few = Prediction(PredictionConfidence.Low) with { Observations = 2, DaysUsed = 2 };

        // Act
        section.Update(NextUpSnapshot(isPremium: true, few));

        // Assert
        section.NapIsPersonal.Should().BeFalse();
        section.NextNap.Should().Contain("60–90 min");
        section.NapWindow.Should().Contain("few more days");
    }

    private HomeSnapshot NextUpSnapshot(bool isPremium, SleepPrediction? sleep)
    {
        var baby = BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var lastFeed = Entry(BreastFeed.Start(BreastSide.Left, Now.AddMinutes(-45)), null);
        var summary = new TodaySummary([], lastFeed, null, null, null, Now.AddMinutes(-30), [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));
        return new HomeSnapshot(settings, baby, summary, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres), IsPremium: isPremium, Sleep: sleep);
    }

    private static SleepPrediction Prediction(PredictionConfidence confidence) => new(
        2, false, Now.AddMinutes(-30), Now.AddMinutes(40), Now.AddMinutes(55), Now.AddMinutes(70), confidence, TimeSpan.FromMinutes(85),
        TimeSpan.FromMinutes(80), 9, 9, WakeWindowPrior.For(61), false, SleepPredictor.Version);

    [Fact]
    public void DailyProgressSection_CalculatesMetricsAndRatiosAgainstAgeNorms()
    {
        // Arrange: 2-month-old baby (born 2026-08-01, tested at 2026-10-01)
        var baby = BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;

        var feed1 = Entry(BreastFeed.Start(BreastSide.Left, Now.AddHours(-6)), null);
        var feed2 = Entry(BreastFeed.Start(BreastSide.Right, Now.AddHours(-3)), null);
        var diaper1 = Entry(new Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Wet), EntryTiming.Point(Now.AddHours(-4)));
        var sleep1 = Entry(new Coo.Domain.CareLog.Kinds.Sleep.SleepDetails(), EntryTiming.Session(TimeRange.Create(Now.AddHours(-5), Now.AddHours(-3)).Value));

        var storedEntries = new StoredEntry[]
        {
            new LiveEntry(feed1),
            new LiveEntry(feed2),
            new LiveEntry(diaper1),
            new LiveEntry(sleep1)
        };

        var summary = new TodaySummary([], feed2, diaper1, sleep1, null, Now.AddHours(-3), storedEntries, DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));
        var snapshot = new HomeSnapshot(settings, baby, summary, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres));

        var section = new DailyProgressSection();

        // Act
        section.Update(snapshot);

        // Assert
        section.FeedsProgress.Should().Contain("2 of 6");
        section.DiapersProgress.Should().Contain("1 of 5+");
        section.SleepProgress.Should().Contain("2 h").And.Contain("14");
        section.FeedsRatio.Should().BeApproximately(2.0 / 6.0, 0.01);
    }

    [Fact]
    public void RecentActivitySection_CapsAtThreeItems()
    {
        // Arrange
        var baby = BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;

        var entries = Enumerable.Range(1, 5).Select(i =>
            (StoredEntry)new LiveEntry(Entry(new Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Wet), EntryTiming.Point(Now.AddMinutes(-i * 10))))
        ).ToList();

        var summary = new TodaySummary([], null, null, null, null, null, entries, DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));
        var snapshot = new HomeSnapshot(settings, baby, summary, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres));

        var section = new RecentActivitySection();

        // Act
        section.Update(snapshot);

        // Assert
        section.Items.Should().HaveCount(3);
    }

    [Fact]
    public void AdvisorTipSection_LoadsStageBadgeAndPediatricTip()
    {
        // Arrange
        var baby = BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var snapshot = new HomeSnapshot(settings, baby, null, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres));

        var section = new AdvisorTipSection();

        // Act
        section.Update(snapshot);

        // Assert
        section.StageBadge.Should().NotBeEmpty();
        section.TipText.Should().NotBeEmpty();
    }

    private CareEntry Entry(EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(++_wall, 0, Device), Now).Value;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
