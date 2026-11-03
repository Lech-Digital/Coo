using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.CareLog.Kinds.SleepPlan;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Insights;

/// <summary>
/// A running sleep plan as it stands today (Spec 019 §2.3–2.4): the day of the plan, the planned day with the accepted
/// changes, the approach for her age now, and the check-in when one is due.
/// </summary>
/// <param name="DayNumber">1 on the day it started.</param>
/// <param name="Day">The planned day; null while paused because she has been unwell.</param>
/// <param name="CheckIn">The check-in due today or missed since, unanswered; null otherwise.</param>
/// <param name="NextCheckIn">The day of the next check-in after any due now.</param>
public sealed record SleepPlanToday(
    CareEntry Entry,
    SleepPlanDetails Details,
    DateOnly StartedOn,
    int DayNumber,
    SettlingApproach Approach,
    SleepPlanDay? Day,
    SleepCheckIn? CheckIn,
    DateOnly NextCheckIn)
{
    public bool IsPaused => Day is null;
}

/// <summary>A plan that ran, for the history (Spec 019 §2.5).</summary>
public sealed record PastSleepPlan(CareEntry Entry, SleepPlanDetails Details, DateOnly StartedOn, DateOnly StoppedOn);

/// <summary>The rules shared by the sleep plan's use cases: the plan's days, its check-ins and the baby's sleeps.</summary>
public static class SleepPlans
{
    /// <summary>The first check-in on day 3, then weekly (Spec 019 §11.2).</summary>
    public const int FirstCheckInDay = 3;

    public const int CheckInEvery = 7;

    /// <summary>How far back the log is read for "unwell lately" (Spec 019 §6).</summary>
    public static readonly TimeSpan UnwellLookBack = TimeSpan.FromDays(3);

    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    public static DateOnly StartedOn(CareEntry entry, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Today(entry.Timing.OccurredAt, zone);
    }

    /// <summary>The check-in day at or before <paramref name="dayNumber"/>, or null before the first.</summary>
    public static int? LatestCheckInDay(int dayNumber) =>
        dayNumber < FirstCheckInDay ? null : FirstCheckInDay + ((dayNumber - FirstCheckInDay) / CheckInEvery * CheckInEvery);

    /// <summary>The check-in day after <paramref name="dayNumber"/>.</summary>
    public static int NextCheckInDay(int dayNumber) => (LatestCheckInDay(dayNumber) ?? FirstCheckInDay - CheckInEvery) + CheckInEvery;

    /// <summary>
    /// The plan's changes since the last new age schedule: those shape the day; the new schedule itself starts it over
    /// for her age then.
    /// </summary>
    public static (DateOnly ScheduledOn, IReadOnlyList<AcceptedAdjustment> Since) Current(SleepPlanDetails plan, DateOnly startedOn)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var reset = plan.Adjustments.LastOrDefault(a => a.Kind == PlanAdjustmentKind.NewAgeSchedule);
        return reset is null
            ? (startedOn, plan.Adjustments)
            : (reset.On, [.. plan.Adjustments.SkipWhile(a => a != reset).Skip(1)]);
    }

    /// <summary>The planned day for this plan now: planned for her age at the last schedule, with the accepted changes.</summary>
    public static Result<SleepPlanDay> Day(
        SleepPlanDetails plan, DateOnly startedOn, BabyProfile baby, IReadOnlyList<SleepSpan> sleeps, bool unwell, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var (scheduledOn, since) = Current(plan, startedOn);
        var intake = plan.Intake;
        if (since.Any(a => a.Kind == PlanAdjustmentKind.DropANap))
            intake = intake with { Goals = intake.Goals.Append(SleepPlanGoal.DropANap).ToHashSet() };
        var age = BabyAge.On(baby, scheduledOn).CorrectedDays;
        var day = SleepPlanner.Plan(intake, new SleepPlanSafety(unwell, false), sleeps, age, now, zone);
        return day.IsFailure ? day : SleepPlanner.Adjust(day.Value, since.Select(a => new PlanAdjustment(a.Kind, a.Minutes)));
    }

    /// <summary>
    /// The nap reminder follows a running plan (Spec 019 §2.3): the predicted window is moved to the plan's next nap or
    /// bedtime still to come today. Without a plan, a paused one, or nothing planned still to come, the prediction is
    /// unchanged; without a prediction (asleep, or nothing logged) there is nothing to remind of.
    /// </summary>
    public static SleepPrediction? FollowPlan(SleepPrediction? predicted, SleepPlanToday? plan, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (predicted is null || plan?.Day is not { } day)
            return predicted;
        var date = Today(now, zone);
        DateTimeOffset At(TimeOnly t)
        {
            var local = date.ToDateTime(t);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        var next = day.Naps.Select(n => (At: At(n.Start), IsBedtime: false))
            .Append((At: At(day.Bedtime), IsBedtime: true))
            .FirstOrDefault(s => s.At > now);
        return next.At == default ? predicted : predicted with { Earliest = next.At, Centre = next.At, Latest = next.At, IsBedtime = next.IsBedtime };
    }

    /// <summary>A temperature of 38 °C or more, or a medicine that is not a vitamin, in the last 3 days.</summary>
    public static bool UnwellLately(IEnumerable<CareEntry> entries, DateTimeOffset now) =>
        entries.Any(e => e.Timing.OccurredAt >= now - UnwellLookBack && e.Timing.OccurredAt <= now
            && (e.Details is TemperatureDetails { IsFever: true } || (e.Details is MedicineDetails m && !Vitamins.IsVitamin(m.Name))));

    internal static async Task<(IReadOnlyList<SleepSpan> Sleeps, bool Unwell)> ReadLogAsync(
        ICareLogStore store, HouseholdId household, BabyId baby, DateTimeOffset from, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entries = (await store.ListAsync(household, from, now.AddMinutes(1), cancellationToken))
            .OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => GetToday.IsAbout(e, baby))
            .ToList();
        var sleeps = entries
            .Select(e => e is { Details: SleepDetails details, Timing: SessionTiming timing }
                ? new SleepSpan(timing.Range.Start, timing.Range.End, details.KindOverride)
                : null)
            .OfType<SleepSpan>()
            .ToList();
        return (sleeps, UnwellLately(entries.Where(e => e.Subject is BabySubject), now));
    }

    internal static IEnumerable<(CareEntry Entry, SleepPlanDetails Details)> Plans(IEnumerable<StoredEntry> stored, BabyId baby) =>
        stored.OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => e.Subject is BabySubject { BabyId: var id } && id == baby)
            .Select(e => (Entry: e, Details: e.Details as SleepPlanDetails))
            .Where(p => p.Details is not null)
            .Select(p => (p.Entry, p.Details!));
}

/// <summary>The baby's running sleep plan today, and her past plans.</summary>
public sealed class GetSleepPlan(ICareLogStore store)
{
    public async Task<SleepPlanToday?> ExecuteAsync(
        HouseholdId household, BabyProfile baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(zone);
        var stored = await store.ListKindAsync(household, SleepPlanKind.Instance.Code, cancellationToken);
        var running = SleepPlans.Plans(stored, baby.Id)
            .Where(p => p.Details.IsRunning && p.Entry.Timing.OccurredAt <= now)
            .OrderByDescending(p => p.Entry.Timing.OccurredAt)
            .FirstOrDefault();
        if (running.Entry is null || !baby.IsBorn)
            return null;

        var (entry, plan) = running;
        var today = SleepPlans.Today(now, zone);
        var startedOn = SleepPlans.StartedOn(entry, zone);
        var dayNumber = today.DayNumber - startedOn.DayNumber + 1;
        var started = entry.Timing.OccurredAt;
        var from = Earlier(started - TimeSpan.FromDays(8), now - SleepPlanner.LookBack - TimeSpan.FromDays(1));
        var (sleeps, unwell) = await SleepPlans.ReadLogAsync(store, household, baby.Id, from, now, cancellationToken);
        var day = SleepPlans.Day(plan, startedOn, baby, sleeps, unwell, now, zone);
        var age = BabyAge.On(baby, today).CorrectedDays;

        // Due: the latest check-in day has come and nothing was answered on or after it.
        SleepCheckIn? checkIn = null;
        var next = SleepPlans.NextCheckInDay(dayNumber);
        if (SleepPlans.LatestCheckInDay(dayNumber) is { } due && day.IsSuccess
            && !plan.Adjustments.Any(a => a.On >= startedOn.AddDays(due - 1)))
            checkIn = SleepPlanner.CheckIn(day.Value, plan.Intake, sleeps, age, started, now, zone);

        return new SleepPlanToday(
            entry, plan, startedOn, dayNumber, SleepPlanner.ApproachFor(plan.Approach, age), day.IsSuccess ? day.Value : null, checkIn,
            startedOn.AddDays(next - 1));
    }

    /// <summary>The stopped plans, newest first.</summary>
    public async Task<IReadOnlyList<PastSleepPlan>> HistoryAsync(
        HouseholdId household, BabyId baby, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var stored = await store.ListKindAsync(household, SleepPlanKind.Instance.Code, cancellationToken);
        return [.. SleepPlans.Plans(stored, baby)
            .Where(p => p.Details.StoppedOn is not null)
            .OrderByDescending(p => p.Entry.Timing.OccurredAt)
            .Select(p => new PastSleepPlan(p.Entry, p.Details, SleepPlans.StartedOn(p.Entry, zone), p.Details.StoppedOn!.Value))];
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>
/// The day a plan would give, before it starts (Spec 019 §2.2): the parent sees the schedule, then starts it. The
/// safety checks of §6 come first: her answers, and fever or medicine in the log.
/// </summary>
public sealed class PreviewSleepPlan(ICareLogStore store)
{
    public async Task<Result<SleepPlanDay>> ExecuteAsync(
        HouseholdId household, BabyProfile baby, SleepPlanIntake intake, bool parentConcern, TimeZoneInfo zone, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        var (sleeps, unwell) = await SleepPlans.ReadLogAsync(
            store, household, baby.Id, now - SleepPlanner.LookBack - TimeSpan.FromDays(1), now, cancellationToken);
        var age = baby.IsBorn ? BabyAge.On(baby, SleepPlans.Today(now, zone)).CorrectedDays : -1;
        return SleepPlanner.Plan(intake, new SleepPlanSafety(unwell, parentConcern), sleeps, age, now, zone);
    }
}

/// <summary>
/// Starts a plan (Spec 019 §2): one running plan per baby, so a running one is stopped today first. Nothing is written
/// when the preview would refuse.
/// </summary>
public sealed class StartSleepPlan(ICareLogStore store, PreviewSleepPlan preview, LogEntry log, ReviseEntry revise)
{
    public async Task<Result<CareEntry>> ExecuteAsync(
        HouseholdId household, BabyProfile baby, SleepPlanIntake intake, bool parentConcern, TimeZoneInfo zone, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(intake);
        var day = await preview.ExecuteAsync(household, baby, intake, parentConcern, zone, now, cancellationToken);
        if (day.IsFailure)
            return Result<CareEntry>.Failure(day.Error!);
        var details = SleepPlanDetails.Create(intake.Goals, intake.Approach, intake.WakeUp);
        if (details.IsFailure)
            return Result<CareEntry>.Failure(details.Error!);

        var today = SleepPlans.Today(now, zone);
        foreach (var (entry, plan) in SleepPlans.Plans(await store.ListKindAsync(household, SleepPlanKind.Instance.Code, cancellationToken), baby.Id))
        {
            if (!plan.IsRunning)
                continue;
            var stopped = await revise.ExecuteAsync(entry.Id, (e, version, at) => e.Revise<SleepPlanDetails>(p => p.Stop(today), version, at), cancellationToken);
            if (stopped.IsFailure)
                return stopped;
        }

        return await log.ExecuteAsync(
            new LogEntryCommand(household, EntrySubject.Baby(baby.Id), details.Value, EntryTiming.Point(TimeZoneInfo.ConvertTime(now, zone)), PersonRef.ThisDevicesPerson, EntrySource.Manual),
            cancellationToken);
    }
}

/// <summary>
/// Answers a check-in (Spec 019 §2.4): the suggestion accepted, or <see cref="PlanAdjustmentKind.KeepGoing"/> when the
/// parent keeps the plan as it is. Either way the check-in is done.
/// </summary>
public sealed class AnswerSleepCheckIn(ReviseEntry revise)
{
    public Task<Result<CareEntry>> ExecuteAsync(EntryId plan, PlanAdjustment answer, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return revise.ExecuteAsync(
            plan,
            (e, version, at) => e.Revise<SleepPlanDetails>(p => p.Accept(new AcceptedAdjustment(today, answer.Kind, answer.Minutes)), version, at),
            cancellationToken);
    }
}

/// <summary>Stops a plan today; it moves to the history.</summary>
public sealed class StopSleepPlan(ReviseEntry revise)
{
    public Task<Result<CareEntry>> ExecuteAsync(EntryId plan, DateOnly today, CancellationToken cancellationToken) =>
        revise.ExecuteAsync(plan, (e, version, at) => e.Revise<SleepPlanDetails>(p => p.Stop(today), version, at), cancellationToken);
}
