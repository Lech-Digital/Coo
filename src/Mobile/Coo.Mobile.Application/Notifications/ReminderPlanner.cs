using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Application.Notifications;

/// <summary>
/// The reminders a parent turned on (ADR 0007 §4: all off until then). Per device, never synced: two partners want
/// different nights.
/// </summary>
/// <param name="FeedEvery">"It has been this long since the last feeding"; null is off.</param>
/// <param name="FeedAtNight">The feeding reminder also during quiet hours, for parents doing night feeds.</param>
/// <param name="TimerRunning">A feeding or pumping timer still running after <see cref="ReminderPlanner.TimerAfter"/>.</param>
/// <param name="NapWindow">Before the predicted sleep window opens (ADR 0008 §7): Premium.</param>
/// <param name="NapLeadMinutes">How long before the window opens: 5, 10, 15 or 20 minutes.</param>
/// <param name="DiaperEvery">"Check the diaper": this long after the last change; null is off.</param>
/// <param name="DiaperAtNight">The diaper reminder also during quiet hours.</param>
/// <param name="BathEveryDays">A bath every 1, 2, 3 or 7 days, counted from the last one; null is off.</param>
/// <param name="BathAt">The time of day for the bath reminder; <see cref="BathTime"/> when unset.</param>
/// <param name="Medicines">Daily medicines and vitamins, each at its own times.</param>
public sealed record ReminderSettings(
    TimeSpan? FeedEvery,
    bool FeedAtNight,
    bool TimerRunning,
    bool NapWindow,
    int NapLeadMinutes = 15,
    TimeSpan? DiaperEvery = null,
    bool DiaperAtNight = false,
    int? BathEveryDays = null,
    TimeOnly? BathAt = null,
    IReadOnlyList<MedicineReminder>? Medicines = null)
{
    public static ReminderSettings Off { get; } = new(null, false, false, false);

    /// <summary>The intervals a parent can pick for the diaper check.</summary>
    public static IReadOnlyList<TimeSpan> DiaperIntervals { get; } = [TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(4)];

    /// <summary>How often a bath can be reminded: daily, every other day, every third day, weekly.</summary>
    public static IReadOnlyList<int> BathIntervals { get; } = [1, 2, 3, 7];

    /// <summary>Bath time, 19:00 until the parent picks another.</summary>
    public TimeOnly BathTime => BathAt ?? new TimeOnly(19, 0);

    public IReadOnlyList<MedicineReminder> MedicineList => Medicines ?? [];

    /// <summary>The lead times a parent can pick for the nap reminder (ADR 0008 §7).</summary>
    public static IReadOnlyList<int> NapLeads { get; } = [5, 10, 15, 20];

    /// <summary>The intervals a parent can pick for the feeding reminder.</summary>
    public static IReadOnlyList<TimeSpan> FeedIntervals { get; } =
        [TimeSpan.FromHours(2), TimeSpan.FromHours(2.5), TimeSpan.FromHours(3), TimeSpan.FromHours(3.5), TimeSpan.FromHours(4)];

    public bool AnyOn => FeedEvery is not null || TimerRunning || NapWindow || DiaperEvery is not null || BathEveryDays is not null || MedicineList.Count > 0;
}

/// <summary>
/// A medicine or vitamin given every day at fixed times: vitamin D at 09:00, an antibiotic at 08:00, 14:00 and 20:00 for a
/// week. <paramref name="Until"/> is the last day, inclusive; null is every day until it is removed.
/// </summary>
public sealed record MedicineReminder(string Name, string? Dose, IReadOnlyList<TimeOnly> Times, DateOnly? Until = null)
{
    public const int MaxTimes = 6;

    /// <summary>The same medicine as a logged one: the same name, or both a vitamin ("Vitamin D" and "D3 drops").</summary>
    public bool Matches(string logged)
    {
        ArgumentNullException.ThrowIfNull(logged);
        return string.Equals(logged.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)
            || (Vitamins.IsVitamin(logged) && Vitamins.IsVitamin(Name));
    }
}

public enum ReminderKind
{
    Feed,
    Timer,
    Nap,
    Bedtime,
    Diaper,
    Bath,
    Medicine,
}

/// <summary>
/// One notification that should be scheduled. <paramref name="Id"/> is stable for its rule and subject, so planning twice
/// changes nothing (ADR 0007 §3). <paramref name="Since"/> is the moment the text names ("last feeding at 11:20"), true
/// whenever the notification arrives, so a late alarm is never wrong. <paramref name="Opens"/> is when a predicted sleep
/// window opens, for the nap and bedtime reminders.
/// </summary>
/// <param name="Medicine">The medicine reminder this one is for.</param>
public sealed record PlannedReminder(
    string Id, ReminderKind Kind, DateTimeOffset At, DateTimeOffset Since, EntryDetails? Details = null, DateTimeOffset? Opens = null,
    MedicineReminder? Medicine = null);

/// <summary>
/// The complete set of reminders that should be scheduled now (ADR 0007 §3): pure, recomputed after every write and
/// sync, and reconciled with Android by the platform. A reminder whose time has passed is not planned: the app already
/// shows it, and a burst of stale notifications after a reboot helps nobody.
/// </summary>
public static class ReminderPlanner
{
    /// <summary>How long a feeding or pumping timer runs before Coo asks whether it was forgotten.</summary>
    public static readonly TimeSpan TimerAfter = TimeSpan.FromMinutes(45);

    /// <param name="sleep">The predicted next sleep window; null when there is none, or without Premium.</param>
    public static IReadOnlyList<PlannedReminder> Plan(
        TodaySummary today, SleepPrediction? sleep, ReminderSettings settings, QuietHours quiet, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(quiet);
        var planned = new List<PlannedReminder>();
        bool AtNight(DateTimeOffset at) => quiet.Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime));

        var feeding = today.Running.Any(e => e.Details is FeedingDetails);
        if (settings.FeedEvery is { } every && !feeding && today.LastFeed is { } last)
        {
            var at = last.Timing.OccurredAt + every;
            if (at > now && (settings.FeedAtNight || !AtNight(at)))
                planned.Add(new PlannedReminder("feed", ReminderKind.Feed, at, last.Timing.OccurredAt, last.Details));
        }

        if (settings.TimerRunning)
        {
            // A timer left running is as likely at 03:00 as at noon, and the parent started it: no quiet hours here.
            foreach (var running in today.Running.Where(e => e.Details is FeedingDetails or PumpDetails))
            {
                var at = running.Timing.OccurredAt + TimerAfter;
                if (at > now)
                    planned.Add(new PlannedReminder($"timer:{running.Id.Value}", ReminderKind.Timer, at, running.Timing.OccurredAt, running.Details));
            }
        }

        var asleep = today.Running.Any(e => e.Details is SleepDetails);
        if (settings.NapWindow && !asleep && sleep is not null)
        {
            // A lead time before the window opens (ADR 0008 §7), never in quiet hours: those are the night's.
            var at = sleep.Earliest - TimeSpan.FromMinutes(settings.NapLeadMinutes);
            if (at > now && !AtNight(at))
            {
                var kind = sleep.IsBedtime ? ReminderKind.Bedtime : ReminderKind.Nap;
                planned.Add(new PlannedReminder("nap", kind, at, sleep.AwakeSince, Opens: sleep.Earliest));
            }
        }

        if (settings.DiaperEvery is { } check && today.LastDiaper is { } diaper)
        {
            var at = diaper.Timing.OccurredAt + check;
            if (at > now && (settings.DiaperAtNight || !AtNight(at)))
                planned.Add(new PlannedReminder("diaper", ReminderKind.Diaper, at, diaper.Timing.OccurredAt, diaper.Details));
        }

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (settings.BathEveryDays is { } days)
        {
            // Due N days after the last bath's day (today when there was none, or it is overdue); bathed today, not before
            // the next due day. Bath time already past today: tomorrow.
            var bathed = today.LastBath is { } bath ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bath.Timing.OccurredAt, zone).DateTime) : (DateOnly?)null;
            var due = bathed is { } d ? d.AddDays(days) : localToday;
            if (due < localToday)
                due = localToday;
            var at = At(due, settings.BathTime, zone);
            if (at <= now)
                at = At(due.AddDays(1), settings.BathTime, zone);
            planned.Add(new PlannedReminder("bath", ReminderKind.Bath, at, today.LastBath?.Timing.OccurredAt ?? at, today.LastBath?.Details));
        }

        var medicines = settings.MedicineList;
        for (var i = 0; i < medicines.Count; i++)
        {
            var medicine = medicines[i];
            var slots = medicine.Times.Distinct().Order().ToList();
            if (slots.Count == 0)
                continue;
            var given = (today.Medicines ?? [])
                .Where(e => e.Details is MedicineDetails m && medicine.Matches(m.Name))
                .Select(e => e.Timing.OccurredAt)
                .ToList();

            // The next day's worth of doses: every slot today and tomorrow still to come, skipping one already given (logged
            // in the three hours before it, and after the dose before it).
            var doses = new[] { localToday, localToday.AddDays(1) }
                .Where(day => medicine.Until is not { } until || day <= until)
                .SelectMany(day => slots.Select(t => At(day, t, zone)))
                .ToList();
            for (var k = 0; k < doses.Count; k++)
            {
                var at = doses[k];
                if (at <= now || at > now.AddDays(1))
                    continue;
                var from = at - TimeSpan.FromHours(3);
                if (k > 0 && doses[k - 1] > from)
                    from = doses[k - 1];
                if (given.Any(g => g > from && g <= at))
                    continue;
                var id = string.Create(CultureInfo.InvariantCulture, $"med:{i}:{at.ToUnixTimeSeconds()}");
                planned.Add(new PlannedReminder(id, ReminderKind.Medicine, at, at, Medicine: medicine));
            }
        }

        return planned;
    }

    /// <summary>A clock time on a local day, as an instant; in a daylight-saving gap the offset before the change.</summary>
    private static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
