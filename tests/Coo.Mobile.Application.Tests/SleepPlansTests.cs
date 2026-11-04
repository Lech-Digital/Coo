using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.CareLog.Kinds.SleepPlan;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Insights;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 019 §2: starting, following, checking in on and stopping a sleep plan on the phone.</summary>
public sealed class SleepPlansTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId BabyId = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    // Monday 2 November 2026, 10:00 in Amsterdam; Laura was born on 2 May, so she is 6 months.
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly BabyProfile Laura =
        BabyProfile.Create(BabyId, Household, "Laura", [], new DateOnly(2026, 5, 2), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 11, 2)).Value;

    private static readonly SleepPlanIntake Intake = new(new HashSet<SleepPlanGoal> { SleepPlanGoal.EarlierBedtime }, SettlingApproach.Fading);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly MemoryStore _store = new();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1;

    public SleepPlansTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task Start_SixMonthBaby_WritesARunningPlanThatTodayShowsAsDayOne()
    {
        // Act
        var started = await Start().ExecuteAsync(Household, Laura, Intake, false, Amsterdam, Now, CancellationToken.None);
        var today = await Get().ExecuteAsync(Household, Laura, Amsterdam, Now, CancellationToken.None);

        // Assert
        started.IsSuccess.Should().BeTrue();
        today.Should().NotBeNull();
        today!.DayNumber.Should().Be(1);
        today.Approach.Should().Be(SettlingApproach.Fading);
        today.Day!.Naps.Should().HaveCount(3);
        today.CheckIn.Should().BeNull();
        today.NextCheckIn.Should().Be(new DateOnly(2026, 11, 4));
    }

    [Fact]
    public async Task Start_AParentsWorry_WritesNothingAndSendsHerToTheDoctor()
    {
        // Act
        var started = await Start().ExecuteAsync(Household, Laura, Intake, true, Amsterdam, Now, CancellationToken.None);

        // Assert
        started.Error!.Code.Should().Be(SleepPlanErrors.SeeDoctor);
        _store.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_AFeverYesterday_RefusesWhileSheIsUnwell()
    {
        // Arrange
        _store.Add(Make(TemperatureDetails.Create(38.4m).Value, EntryTiming.Point(Now.AddDays(-1))));

        // Act
        var started = await Start().ExecuteAsync(Household, Laura, Intake, false, Amsterdam, Now, CancellationToken.None);

        // Assert
        started.Error!.Code.Should().Be(SleepPlanErrors.Unwell);
        _store.Entries.OfType<CareEntry>().Should().NotContain(e => e.Details is SleepPlanDetails);
    }

    [Fact]
    public async Task Start_WhileAnotherRuns_StopsTheOldOneToday()
    {
        // Arrange
        await Start().ExecuteAsync(Household, Laura, Intake, false, Amsterdam, Now.AddDays(-5), CancellationToken.None);

        // Act
        await Start().ExecuteAsync(Household, Laura, Intake with { Approach = SettlingApproach.Chair }, false, Amsterdam, Now, CancellationToken.None);

        // Assert
        var plans = _store.Entries.Select(e => e.Details).OfType<SleepPlanDetails>().ToList();
        plans.Should().HaveCount(2);
        plans.Should().ContainSingle(p => p.IsRunning).Which.Approach.Should().Be(SettlingApproach.Chair);
        plans.Should().ContainSingle(p => p.StoppedOn == new DateOnly(2026, 11, 2));
    }

    [Fact]
    public async Task Get_OnDayThree_HasACheckInUntilItIsAnswered()
    {
        // Arrange
        var started = await Start().ExecuteAsync(Household, Laura, Intake, false, Amsterdam, Now, CancellationToken.None);
        var dayThree = Now.AddDays(2);

        // Act
        var due = await Get().ExecuteAsync(Household, Laura, Amsterdam, dayThree, CancellationToken.None);
        await new AnswerSleepCheckIn(Revise()).ExecuteAsync(started.Value.Id, new PlanAdjustment(PlanAdjustmentKind.KeepGoing), new DateOnly(2026, 11, 4), CancellationToken.None);
        var answered = await Get().ExecuteAsync(Household, Laura, Amsterdam, dayThree, CancellationToken.None);

        // Assert
        due!.DayNumber.Should().Be(3);
        due.CheckIn.Should().NotBeNull();
        answered!.CheckIn.Should().BeNull();
        answered.NextCheckIn.Should().Be(new DateOnly(2026, 11, 11));
    }

    [Fact]
    public async Task Get_AnEarlierBedtimeAccepted_MovesTheBedtimeOfThePlannedDay()
    {
        // Arrange
        var started = await Start().ExecuteAsync(Household, Laura, Intake, false, Amsterdam, Now, CancellationToken.None);
        var before = (await Get().ExecuteAsync(Household, Laura, Amsterdam, Now, CancellationToken.None))!.Day!;

        // Act
        await new AnswerSleepCheckIn(Revise()).ExecuteAsync(started.Value.Id, new PlanAdjustment(PlanAdjustmentKind.EarlierBedtime, 15), new DateOnly(2026, 11, 4), CancellationToken.None);
        var after = (await Get().ExecuteAsync(Household, Laura, Amsterdam, Now.AddDays(2), CancellationToken.None))!.Day!;

        // Assert
        after.Bedtime.Should().BeBefore(before.Bedtime);
    }

    [Fact]
    public async Task Stop_ARunningPlan_LeavesNoneRunningAndOneInTheHistory()
    {
        // Arrange
        var started = await Start().ExecuteAsync(Household, Laura, Intake, false, Amsterdam, Now, CancellationToken.None);

        // Act
        await new StopSleepPlan(Revise()).ExecuteAsync(started.Value.Id, new DateOnly(2026, 11, 9), CancellationToken.None);

        // Assert
        (await Get().ExecuteAsync(Household, Laura, Amsterdam, Now.AddDays(7), CancellationToken.None)).Should().BeNull();
        (await Get().HistoryAsync(Household, BabyId, Amsterdam, CancellationToken.None)).Should().ContainSingle()
            .Which.StoppedOn.Should().Be(new DateOnly(2026, 11, 9));
    }

    [Theory]
    [InlineData(1, null, 3)]
    [InlineData(2, null, 3)]
    [InlineData(3, 3, 10)]
    [InlineData(9, 3, 10)]
    [InlineData(10, 10, 17)]
    [InlineData(30, 24, 31)]
    public void CheckInDays_FirstOnDayThreeThenWeekly(int dayNumber, int? latest, int next)
    {
        // Act and Assert
        SleepPlans.LatestCheckInDay(dayNumber).Should().Be(latest);
        SleepPlans.NextCheckInDay(dayNumber).Should().Be(next);
    }

    [Fact]
    public void UnwellLately_AVitaminOrAnOldFever_IsNotUnwell()
    {
        // Arrange
        var entries = new[]
        {
            Make(MedicineDetails.Create("Vitamin D", null).Value, EntryTiming.Point(Now.AddHours(-2))),
            Make(TemperatureDetails.Create(38.6m).Value, EntryTiming.Point(Now.AddDays(-5))),
        };

        // Act and Assert
        SleepPlans.UnwellLately(entries, Now).Should().BeFalse();
        SleepPlans.UnwellLately([.. entries, Make(MedicineDetails.Create("Paracetamol", "2.5 ml").Value, EntryTiming.Point(Now.AddHours(-3)))], Now)
            .Should().BeTrue();
    }

    [Fact]
    public async Task FollowPlan_ARunningPlan_MovesTheNapReminderToThePlansNextSleep()
    {
        // Arrange: the plan's day, and a predicted window an hour from now.
        await Start().ExecuteAsync(Household, Laura, Intake, false, Amsterdam, Now, CancellationToken.None);
        var plan = await Get().ExecuteAsync(Household, Laura, Amsterdam, Now, CancellationToken.None);
        var predicted = SleepPredictor.Predict([new SleepSpan(Now.AddHours(-12), Now.AddHours(-1))], 183, Now, Amsterdam);
        var next = plan!.Day!.Naps.Select(n => n.Start).Append(plan.Day.Bedtime).First(t => t > TimeOnly.FromDateTime(Now.DateTime));

        // Act
        var followed = SleepPlans.FollowPlan(predicted, plan, Now, Amsterdam);
        var withoutPlan = SleepPlans.FollowPlan(predicted, null, Now, Amsterdam);

        // Assert
        predicted.Should().NotBeNull();
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(followed!.Earliest, Amsterdam).DateTime).Should().Be(next);
        followed.AwakeSince.Should().Be(predicted!.AwakeSince);
        withoutPlan.Should().BeSameAs(predicted);
    }

    /// <summary>
    /// Spec 019 §10's deliverable, without a phone: a baby at 2 and at 6 months with two weeks of logged sleep (bedtime at
    /// 21:30, two night wakings), a plan started, and on day 3 a check-in with measures from both weeks and one bounded
    /// suggestion; at 2 months the plan is rhythm and routine only.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task Deliverable_SeededLog_PlanAndDayThreeCheckInMeasureAndSuggestOneBoundedChange(int months)
    {
        // Arrange
        var born = DateOnly.FromDateTime(Now.Date).AddMonths(-months);
        var baby = BabyProfile.Create(BabyId, Household, "Laura", [], born, null, new HlcStamp(1, 0, Device), born).Value;
        var dayThree = Now.AddDays(2);
        Seed(Now.AddDays(-14), dayThree, napsPerDay: months < 4 ? 4 : 2);

        // Act
        var started = await Start().ExecuteAsync(Household, baby, Intake, false, Amsterdam, Now, CancellationToken.None);
        var first = await Get().ExecuteAsync(Household, baby, Amsterdam, Now, CancellationToken.None);
        var due = await Get().ExecuteAsync(Household, baby, Amsterdam, dayThree, CancellationToken.None);

        // Assert
        started.IsSuccess.Should().BeTrue();
        first!.Approach.Should().Be(months < 4 ? SettlingApproach.Rhythm : SettlingApproach.Fading);
        first.Day!.Naps.Should().NotBeEmpty();
        first.Day.Bedtime.Should().BeOnOrBefore(new TimeOnly(21, 0), "the plan moves the late bedtime toward her age's");
        var checkIn = due!.CheckIn;
        checkIn.Should().NotBeNull();
        checkIn!.Before.Days.Should().BeGreaterThan(0);
        checkIn.Now.Days.Should().BeGreaterThan(0);
        checkIn.Before.NightWakings.Should().NotBeNull();
        checkIn.Suggestion.Minutes.Should().BeInRange(0, 30, "a check-in suggests a small change");
    }

    /// <summary>Every day: wake at 06:30, naps of an hour spread over the day, bedtime 21:30 with two wakings in the night.</summary>
    private void Seed(DateTimeOffset from, DateTimeOffset to, int napsPerDay)
    {
        for (var day = TimeZoneInfo.ConvertTime(from, Amsterdam).Date; day < TimeZoneInfo.ConvertTime(to, Amsterdam).Date; day = day.AddDays(1))
        {
            DateTimeOffset At(double hours) => new(day.AddHours(hours), Amsterdam.GetUtcOffset(day.AddHours(hours)));
            var gap = 12.0 / (napsPerDay + 1);
            for (var nap = 1; nap <= napsPerDay; nap++)
                Sleep(At(6.5 + (gap * nap)), At(7.5 + (gap * nap) - 0.5));
            Sleep(At(21.5), At(25));
            Sleep(At(25.25), At(28));
            Sleep(At(28.25), At(30.5));
        }

        void Sleep(DateTimeOffset start, DateTimeOffset end)
        {
            if (end <= Now.AddDays(2))
                _store.Add(Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value), end > Now ? end : Now));
        }
    }

    private StartSleepPlan Start() => new(_store, new PreviewSleepPlan(_store), new LogEntry(_store, _clock, _time), Revise());

    private GetSleepPlan Get() => new(_store);

    private ReviseEntry Revise() => new(_store, _clock, _time);

    private CareEntry Make(EntryDetails details, EntryTiming timing, DateTimeOffset? recordedAt = null) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(BabyId), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), recordedAt ?? Now).Value;

    private sealed class MemoryStore : ICareLogStore
    {
        private readonly Dictionary<EntryId, CareEntry> _entries = [];

        public IReadOnlyList<CareEntry> Entries => [.. _entries.Values];

        public void Add(CareEntry entry) => _entries[entry.Id] = entry;

        public Task SaveAsync(CareEntry entry, CancellationToken cancellationToken)
        {
            _entries[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<StoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken) =>
            Task.FromResult<StoredEntry?>(_entries.TryGetValue(id, out var e) ? new LiveEntry(e) : null);

        public Task<IReadOnlyList<StoredEntry>> ListAsync(HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredEntry>>([.. _entries.Values
                .Where(e => e.Timing.OccurredAt >= windowStart && e.Timing.OccurredAt < windowEnd)
                .OrderByDescending(e => e.Timing.OccurredAt)
                .Select(e => new LiveEntry(e))]);

        public Task<bool> DeleteAsync(EntryId id, HlcStamp version, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> WriteAllAsync(CareLogWrites changes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoreCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountAllEntriesAsync(HouseholdId household, CancellationToken cancellationToken) => Task.FromResult(_entries.Count);
    }
}
