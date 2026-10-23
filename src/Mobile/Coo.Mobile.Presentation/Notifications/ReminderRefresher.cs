using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
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
    IReminderSettingsStore settings,
    IDevicePreferences preferences,
    IReminderScheduler scheduler,
    IHomeWidget widget,
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
            if (household is null || baby is null)
            {
                await scheduler.ReplaceAllAsync([], cancellationToken);
                widget.Show(new WidgetContent("Coo", Strings.Get("Widget.Open"), "", ""));
                return;
            }

            var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
            var now = time.GetUtcNow();
            var formats = new Formats(clock.Is24Hour, household.VolumeUnit);
            var today = baby.IsBorn ? await getToday.ExecuteAsync(household.Id, baby.Id, zone, now, cancellationToken) : null;

            widget.Show(Widget(baby, today, formats, zone));

            var chosen = await settings.GetAsync(cancellationToken);
            if (today is null || !chosen.AnyOn)
            {
                await scheduler.ReplaceAllAsync([], cancellationToken);
                return;
            }

            var quiet = (await preferences.GetAppearanceAsync(cancellationToken)).QuietHours;
            var planned = ReminderPlanner.Plan(today, baby.BirthDate, chosen, quiet, zone, now);
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
            _ => (Strings.Format("Reminder.Nap.Title", babyName), Strings.Format("Reminder.Nap.Body", since)),
        };
        return new ReminderNotice(reminder.Id, reminder.At, title, body);
    }

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
