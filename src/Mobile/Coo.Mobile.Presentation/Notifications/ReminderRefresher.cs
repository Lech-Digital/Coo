using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Households;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Insights;
using Coo.Mobile.Application.Notifications;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Notifications;

/// <summary>
/// Keeps Android's reminders and the home-screen widget in step with the log (ADR 0007 §3): after every local write,
/// every sync that brought something, app start and a settings change. Texts are composed here, in the app's language,
/// and name absolute times, so a late alarm or an old widget is never wrong.
/// </summary>
public sealed class ReminderRefresher(
    IHouseholdStore households,
    GetToday getToday,
    GetSleepPrediction getSleep,
    IBillingService billing,
    IReminderSettingsStore settings,
    IDevicePreferences preferences,
    IReminderScheduler scheduler,
    IHomeWidget widget,
    ITimerNotifications timers,
    IClockFormat clock,
    TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requested;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// After a write or a sync: plans again a second later, once for a burst (a feed and its switch, a pull of many
    /// records). Safe from any thread.
    /// </summary>
    public void RequestSoon()
    {
        if (Interlocked.Exchange(ref _requested, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), time);
            Interlocked.Exchange(ref _requested, 0);
            await RefreshAsync();
        });
    }

    /// <summary>Plans again and replaces what is scheduled. Never throws: a reminder failing must not fail a write.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var household = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => !h.IsDeleted);
            var baby = household is null ? null
                : (await households.ListBabiesAsync(household.Id, cancellationToken)).FirstOrDefault(b => !b.IsDeleted);
            var labels = Labels();
            if (household is null || baby is null)
            {
                await scheduler.ReplaceAllAsync([], cancellationToken);
                widget.Show(new WidgetContent("Coo", Strings.Get("Widget.Open"), "", "", Labels: labels, HasBaby: false));
                timers.Show([], labels);
                return;
            }

            var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
            var now = time.GetUtcNow();
            var formats = new Formats(clock.Is24Hour, household.VolumeUnit);
            var today = baby.IsBorn ? await getToday.ExecuteAsync(household.Id, baby.Id, zone, now, cancellationToken) : null;

            // The personal prediction is Premium (ADR 0008 §7): the widget's next-sleep line and the nap reminder.
            var sleep = baby.IsBorn && billing.IsPremiumCached
                ? await getSleep.ExecuteAsync(household.Id, baby, zone, now, cancellationToken)
                : null;
            var running = Running(today, formats, zone);
            widget.Show(Widget(baby, today, formats, zone) with
            {
                LastFeedAt = today?.LastFeed?.Timing.OccurredAt,
                AwakeSince = today?.Running.Any(e => e.Details is SleepDetails) == true ? null : today?.AwakeSince,
                AsleepSince = today?.Running.FirstOrDefault(e => e.Details is SleepDetails)?.Timing.OccurredAt,
                Running = running,
                NextSleep = sleep is { Confidence: Coo.Domain.Insights.PredictionConfidence.High }
                    ? Strings.Format(sleep.IsBedtime ? "Today.BedLikely" : "Today.NapLikely", formats.Time(sleep.Earliest, zone), formats.Time(sleep.Latest, zone))
                    : "",
                Labels = labels,
            });
            timers.Show(running, labels);

            var chosen = await settings.GetAsync(cancellationToken);
            if (today is null || !chosen.AnyOn)
            {
                await scheduler.ReplaceAllAsync([], cancellationToken);
                return;
            }

            var quiet = (await preferences.GetAppearanceAsync(cancellationToken)).QuietHours;
            if (!chosen.NapWindow)
                sleep = null;
            var planned = ReminderPlanner.Plan(today, sleep, chosen, quiet, zone, now);
            await scheduler.ReplaceAllAsync([.. planned.Select(p => Notice(p, baby.Name, formats, zone))], cancellationToken);
        }
#pragma warning disable CA1031 // Reminders are best effort: the next write or sync plans again.
        catch (Exception e)
#pragma warning restore CA1031
        {
            System.Diagnostics.Debug.WriteLine($"Reminders not refreshed: {e.GetType().Name}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The words of one planned reminder.</summary>
    public static ReminderNotice Notice(PlannedReminder reminder, string babyName, Formats formats, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentNullException.ThrowIfNull(formats);
        var since = formats.Time(reminder.Since, zone);
        var (title, body) = reminder.Kind switch
        {
            ReminderKind.Feed => (Strings.Format("Reminder.Feed.Title", babyName), Strings.Format("Reminder.Feed.Body", since)),
            ReminderKind.Timer => (
                Strings.Get(reminder.Details is FeedingDetails ? "Reminder.Timer.Feeding" : "Reminder.Timer.Pumping"),
                Strings.Format("Reminder.Timer.Body", since)),
            ReminderKind.Bedtime => (
                Strings.Format("Reminder.Bed.Title", babyName),
                Strings.Format("Reminder.Bed.Body", since, formats.Time(reminder.Opens ?? reminder.At, zone))),
            ReminderKind.Diaper => (Strings.Format("Reminder.Diaper.Title", babyName), Strings.Format("Reminder.Diaper.Body", since)),
            ReminderKind.Bath => (
                Strings.Format("Reminder.Bath.Title", babyName),
                reminder.Since < reminder.At
                    ? Strings.Format("Reminder.Bath.Body", TimeZoneInfo.ConvertTime(reminder.Since, zone).ToString("dddd d MMMM", System.Globalization.CultureInfo.CurrentCulture))
                    : Strings.Get("Reminder.Bath.First")),
            ReminderKind.Medicine when reminder.Medicine is { } medicine => (
                Strings.Format("Reminder.Medicine.Title", medicine.Name, babyName),
                medicine.Dose is { Length: > 0 } dose
                    ? Strings.Format("Reminder.Medicine.BodyDose", dose, formats.Time(reminder.At, zone))
                    : Strings.Format("Reminder.Medicine.Body", formats.Time(reminder.At, zone))),
            _ => (Strings.Format("Reminder.Nap.Title", babyName), Strings.Format("Reminder.Nap.Body", since, formats.Time(reminder.Opens ?? reminder.At, zone))),
        };
        return new ReminderNotice(reminder.Id, reminder.At, title, body, Buttons(reminder));
    }

    /// <summary>The answers a notification offers, so a change or a dose is logged without opening Coo.</summary>
    private static IReadOnlyList<ReminderButton>? Buttons(PlannedReminder reminder) => reminder.Kind switch
    {
        ReminderKind.Diaper =>
        [
            new(Strings.Get("Diaper.Wet"), QuickAction.Wet),
            new(Strings.Get("Diaper.Dirty"), QuickAction.Dirty),
            new(Strings.Get("Diaper.Mixed"), QuickAction.Mixed),
        ],
        ReminderKind.Bath => [new(Strings.Get("Reminder.Bath.Done"), QuickAction.Bath)],
        ReminderKind.Medicine when reminder.Medicine is { } m => [new(Strings.Get("Reminder.Medicine.Given"), QuickAction.Medicine, m.Name, m.Dose)],
        _ => null,
    };

    /// <summary>What runs now, for the widgets' buttons and the timer notification, with its words in the app's language.</summary>
    public static IReadOnlyList<RunningTimer> Running(TodaySummary? today, Formats formats, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (today is null)
            return [];
        var timers = new List<RunningTimer>();
        foreach (var entry in today.Running)
        {
            var since = entry.Timing.OccurredAt;
            var started = Strings.Format("Timers.Since", formats.Time(since, zone));
            switch (entry.Details)
            {
                case BreastFeed feed when feed.Segments[^1].End is not null:
                    timers.Add(new RunningTimer(RunningKind.FeedPaused, since, Strings.Get("Hero.FeedingPaused"), started));
                    break;
                case BreastFeed feed:
                    var left = feed.LastSide == BreastSide.Left;
                    timers.Add(new RunningTimer(left ? RunningKind.FeedLeft : RunningKind.FeedRight, since,
                        Strings.Get(left ? "Hero.FeedingLeft" : "Hero.FeedingRight"), started));
                    break;
                case SleepDetails:
                    timers.Add(new RunningTimer(RunningKind.Sleep, since, Strings.Get("Hero.Sleeping"), started));
                    break;
                case Coo.Domain.CareLog.Kinds.Pump.PumpDetails:
                    timers.Add(new RunningTimer(RunningKind.Pump, since, Strings.Get("Hero.Pumping"), started));
                    break;
            }
        }

        return timers;
    }

    /// <summary>Every widget and notification text, in the app's language at the time of the refresh.</summary>
    public static IReadOnlyDictionary<string, string> Labels() => new Dictionary<string, string>
    {
        [WidgetLabels.FeedLeft] = Strings.Get("Feed.Left"),
        [WidgetLabels.FeedRight] = Strings.Get("Feed.Right"),
        [WidgetLabels.Switch] = Strings.Get("Widget.Switch"),
        [WidgetLabels.Pause] = Strings.Get("Action.Pause"),
        [WidgetLabels.Stop] = Strings.Get("Action.Stop"),
        [WidgetLabels.Wet] = Strings.Get("Diaper.Wet"),
        [WidgetLabels.Dirty] = Strings.Get("Diaper.Dirty"),
        [WidgetLabels.Sleep] = Strings.Get("Kind.Sleep"),
        [WidgetLabels.WokeUp] = Strings.Get("Action.WokeUp"),
        [WidgetLabels.Pump] = Strings.Get("Kind.Pump"),
        [WidgetLabels.StopPump] = Strings.Get("Widget.StopPump"),
        [WidgetLabels.LastFeed] = Strings.Get("Widget.LastFeed"),
        [WidgetLabels.Awake] = Strings.Get("Widget.Awake"),
        [WidgetLabels.Asleep] = Strings.Get("Widget.Asleep"),
        [WidgetLabels.Tell] = Strings.Get("Widget.Tell"),
        [WidgetLabels.Open] = Strings.Get("Widget.Open"),
        [WidgetLabels.Channel] = Strings.Get("Timers.Channel"),
        [WidgetLabels.TimerHidden] = Strings.Get("Timers.Hidden"),
    };

    /// <summary>The widget's lines: the last feeding, sleep or awake, the last diaper, each with its clock time.</summary>
    public static WidgetContent Widget(BabyProfile baby, TodaySummary? today, Formats formats, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(formats);
        if (today is null)
            return new WidgetContent(baby.Name, Strings.Get("Widget.Open"), "", "");

        var feeding = today.Running.FirstOrDefault(e => e.Details is FeedingDetails);
        var feed = feeding is not null ? Strings.Format("Widget.FeedingSince", formats.Time(feeding.Timing.OccurredAt, zone))
            : today.LastFeed is { } last ? Strings.Format("Widget.FedAt", formats.Time(last.Timing.OccurredAt, zone))
            : Strings.Get("Widget.NoFeed");

        var sleeping = today.Running.FirstOrDefault(e => e.Details is SleepDetails);
        var sleep = sleeping is not null ? Strings.Format("Widget.AsleepSince", formats.Time(sleeping.Timing.OccurredAt, zone))
            : today.AwakeSince is { } awake ? Strings.Format("Widget.AwakeSince", formats.Time(awake, zone))
            : "";

        var diaper = today.LastDiaper is { } d ? Strings.Format("Widget.DiaperAt", formats.Time(d.Timing.OccurredAt, zone)) : Strings.Get("Widget.NoDiaper");
        return new WidgetContent(baby.Name, feed, sleep, diaper);
    }
}
