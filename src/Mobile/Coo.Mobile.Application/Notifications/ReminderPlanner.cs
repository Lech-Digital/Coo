using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
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
/// <param name="NapWindow">Shortly before the end of the baby's wake window.</param>
public sealed record ReminderSettings(TimeSpan? FeedEvery, bool FeedAtNight, bool TimerRunning, bool NapWindow)
{
    public static ReminderSettings Off { get; } = new(null, false, false, false);

    /// <summary>The intervals a parent can pick for the feeding reminder.</summary>
    public static IReadOnlyList<TimeSpan> FeedIntervals { get; } =
        [TimeSpan.FromHours(2), TimeSpan.FromHours(2.5), TimeSpan.FromHours(3), TimeSpan.FromHours(3.5), TimeSpan.FromHours(4)];

    public bool AnyOn => FeedEvery is not null || TimerRunning || NapWindow;
}

public enum ReminderKind
{
    Feed,
    Timer,
    Nap,
}

/// <summary>
/// One notification that should be scheduled. <paramref name="Id"/> is stable for its rule and subject, so planning twice
/// changes nothing (ADR 0007 §3). <paramref name="Since"/> is the moment the text names ("last feeding at 11:20"), true
/// whenever the notification arrives, so a late alarm is never wrong.
/// </summary>
public sealed record PlannedReminder(string Id, ReminderKind Kind, DateTimeOffset At, DateTimeOffset Since, EntryDetails? Details = null);

/// <summary>Typical wake windows by age, in minutes: how long a baby manages awake before the next sleep.</summary>
public static class WakeWindows
{
    public static (int MinMinutes, int MaxMinutes) For(int daysOld) => daysOld switch
    {
        < 14 => (30, 45),
        < 45 => (45, 60),
        < 90 => (60, 90),
        < 150 => (90, 120),
        < 240 => (120, 150),
        < 365 => (150, 210),
        < 545 => (240, 300),
        _ => (300, 360),
    };
}

/// <summary>
/// The complete set of reminders that should be scheduled now (ADR 0007 §3): pure, recomputed after every write and
/// sync, and reconciled with Android by the platform. A reminder whose time has passed is not planned: the app already
/// shows it, and a burst of stale notifications after a reboot helps nobody.
/// </summary>
public static class ReminderPlanner
{
    /// <summary>How long a feeding or pumping timer runs before Coo asks whether it was forgotten.</summary>
    public static readonly TimeSpan TimerAfter = TimeSpan.FromMinutes(45);

    /// <summary>How long before the end of the wake window the nap reminder comes (ADR 0008 §7).</summary>
    public static readonly TimeSpan NapLead = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<PlannedReminder> Plan(
        TodaySummary today, DateOnly? birthDate, ReminderSettings settings, QuietHours quiet, TimeZoneInfo zone, DateTimeOffset now)
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
        if (settings.NapWindow && !asleep && birthDate is { } born && today.AwakeSince is { } awake)
        {
            var daysOld = Math.Max(0, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime).DayNumber - born.DayNumber);
            var at = awake.AddMinutes(WakeWindows.For(daysOld).MaxMinutes) - NapLead;
            // Not at night: the next sleep then is the night's, and nobody needs a reminder for it.
            if (at > now && !AtNight(at))
                planned.Add(new PlannedReminder("nap", ReminderKind.Nap, at, awake));
        }

        return planned;
    }
}
