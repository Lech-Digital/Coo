using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.SleepPlan;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.Insights;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.SleepPlan;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 019: every goal, approach, note and suggestion has its words in every language, and Today's plan line.</summary>
[Collection(nameof(StringsState))]
public sealed class SleepPlanTextTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);
    private static readonly Formats Clock24 = new(true, VolumeUnit.Millilitres);

    public SleepPlanTextTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    public static TheoryData<string> Languages => ["en", "de", "fr", "es", "it", "pl", "nl"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Keys_EveryGoalApproachNoteAndSuggestion_HasItsText(string language)
    {
        // Arrange
        Strings.Use(new LanguagePreference(language), null);
        var keys = Enum.GetValues<SleepPlanGoal>().Select(g => "Plan.Goal." + g)
            .Concat(Enum.GetValues<SettlingApproach>().SelectMany(a => new[] { "Plan.Approach." + a, "Plan.Text." + a }))
            .Concat(Enum.GetValues<PlanNote>().Select(n => "Plan.Note." + n))
            .Concat(Enum.GetValues<PlanAdjustmentKind>().Select(k => "Plan.Adj." + k))
            .Concat(new[] { SleepPlanErrors.TooYoung, SleepPlanErrors.Unwell, SleepPlanErrors.SeeDoctor }.Select(c => "Error." + c));

        // Act
        var missing = keys.Where(k => Strings.Get(k) == k).ToList();

        // Assert
        missing.Should().BeEmpty();
    }

    [Fact]
    public void TodayLine_DuringTheDay_NamesTheDayAndTheNextPlannedSleep()
    {
        // Arrange: 8 months, a day from 07:00 with two naps.
        var plan = Running(dayNumber: 3, checkIn: null);
        var firstNap = plan.Day!.Naps[0].Start;

        // Act
        var morning = SleepPlanViewModel.TodayLine(plan, new TimeOnly(8, 0), Clock24);
        var evening = SleepPlanViewModel.TodayLine(plan, plan.Day.Naps[^1].Start.AddMinutes(1), Clock24);
        var night = SleepPlanViewModel.TodayLine(plan, new TimeOnly(23, 0), Clock24);

        // Assert
        morning.Should().Be($"Sleep plan · day 3 · Nap 1 at {firstNap:HH:mm}");
        evening.Should().Be($"Sleep plan · day 3 · Bedtime at {plan.Day.Bedtime:HH:mm}");
        night.Should().Be("Sleep plan · day 3");
    }

    [Fact]
    public void TodayLine_ACheckInDueOrNoPlan_SaysTheCheckInIsReadyOrNothing()
    {
        // Arrange
        var checkIn = new SleepCheckIn(Empty, Empty, new PlanAdjustment(PlanAdjustmentKind.KeepGoing));

        // Act
        var due = SleepPlanViewModel.TodayLine(Running(dayNumber: 10, checkIn), new TimeOnly(8, 0), Clock24);
        var none = SleepPlanViewModel.TodayLine(null, new TimeOnly(8, 0), Clock24);

        // Assert
        due.Should().Be("Sleep plan · day 10 · check-in ready");
        none.Should().BeNull();
    }

    private static readonly SleepMeasures Empty = new(0, null, null, null, null, null, null, null, null);

    private static SleepPlanToday Running(int dayNumber, SleepCheckIn? checkIn)
    {
        var details = SleepPlanDetails.Create([SleepPlanGoal.LongerNaps], SettlingApproach.Fading, new TimeOnly(7, 0)).Value;
        var entry = CareEntry.Create(
            new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1")), Household, EntrySubject.Baby(Baby), details, EntryTiming.Point(T0),
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(1, 0, Device), T0).Value;
        var day = SleepPlanner.Plan(details.Intake, new SleepPlanSafety(false, false), [], 250, T0, TimeZoneInfo.Utc).Value;
        var started = DateOnly.FromDateTime(T0.DateTime);
        return new SleepPlanToday(entry, details, started, dayNumber, SettlingApproach.Fading, day, checkIn, started.AddDays(9));
    }
}
