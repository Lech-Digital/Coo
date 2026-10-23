using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
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
public sealed record ReminderSettings(TimeSpan? FeedEvery, bool FeedAtNight, bool TimerRunning, bool NapWindow, int NapLeadMinutes = 15)
{
    public static ReminderSettings Off { get; } = new(null, false, false, false);

    /// <summary>The lead times a parent can pick for the nap reminder (ADR 0008 §7).</summary>
    public static IReadOnlyList<int> NapLeads { get; } = [5, 10, 15, 20];

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
    Bedtime,
}

/// <summary>
/// One notification that should be scheduled. <paramref name="Id"/> is stable for its rule and subject, so planning twice
/// changes nothing (ADR 0007 §3). <paramref name="Since"/> is the moment the text names ("last feeding at 11:20"), true
/// whenever the notification arrives, so a late alarm is never wrong. <paramref name="Opens"/> is when a predicted sleep
/// window opens, for the nap and bedtime reminders.
/// </summary>
public sealed record PlannedReminder(
    string Id, ReminderKind Kind, DateTimeOffset At, DateTimeOffset Since, EntryDetails? Details = null, DateTimeOffset? Opens = null);

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

        return planned;
    }
}
