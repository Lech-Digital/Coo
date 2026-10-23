using Coo.Domain.CareLog.Kinds.Sleep;

namespace Coo.Domain.Insights;

/// <summary>A sleep as the predictor sees it: its times, and the person's own label if they gave one.</summary>
/// <param name="End">Null while the baby sleeps.</param>
public sealed record SleepSpan(DateTimeOffset Start, DateTimeOffset? End, SleepKindOverride? Override = null)
{
    public TimeSpan? Length => End - Start;
}

public enum SleepClass
{
    Nap,
    Night,
}

/// <summary>When nights start, in the household's local time; it may run across midnight.</summary>
public sealed record NightWindow(TimeOnly From, TimeOnly To)
{
    /// <summary>Until there are enough nights to learn from (ADR 0008 §3).</summary>
    public static NightWindow Default { get; } = new(new TimeOnly(18, 30), new TimeOnly(23, 0));

    /// <summary>Nights go on until this time: a sleep or a waking before it, after the window opened, is part of the night.</summary>
    public static readonly TimeOnly Morning = new(5, 0);

    public bool Contains(TimeOnly time) => From <= To ? time >= From && time <= To : time >= From || time <= To;

    /// <summary>Between the window's start and the morning: the night, when nobody wants a nap suggested.</summary>
    public bool IsNightTime(TimeOnly time) => time >= From || time < Morning;
}

/// <summary>
/// Labels each sleep a night or a nap (ADR 0008 §3). A person's label always wins. Otherwise a sleep is night when it
/// lasts 5 hours, or 3 hours starting inside the night window; and a sleep that follows a night within a short waking,
/// before the morning, continues that night, so a young baby's broken night does not count as naps.
/// </summary>
public static class SleepClassifier
{
    /// <summary>Nights needed before the window is learned rather than the default.</summary>
    public const int NightsToLearn = 3;

    public static readonly TimeSpan NightLength = TimeSpan.FromHours(5);
    public static readonly TimeSpan NightInWindowLength = TimeSpan.FromHours(3);
    public static readonly TimeSpan NightWakingAtMost = TimeSpan.FromHours(3);

    /// <summary>
    /// The middle 80% of the night-sleep start times (ADR 0008 §3), from the long or labelled nights. Start times are
    /// counted from noon, so a window can run across midnight.
    /// </summary>
    public static NightWindow LearnNightWindow(IEnumerable<SleepSpan> sleeps, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(zone);
        var starts = sleeps
            .Where(s => s.End is not null && (s.Override == SleepKindOverride.Night || (s.Override is null && s.Length >= NightLength)))
            .Select(s => FromNoon(Local(s.Start, zone)))
            .Order()
            .ToList();
        if (starts.Count < NightsToLearn)
            return NightWindow.Default;

        var from = starts[(int)Math.Floor(0.1 * (starts.Count - 1))];
        var to = starts[(int)Math.Ceiling(0.9 * (starts.Count - 1))];
        return new NightWindow(ToTime(from), ToTime(to));
    }

    /// <summary>Classifies completed sleeps, oldest first; the result is in the same order.</summary>
    public static IReadOnlyList<SleepClass> Classify(IReadOnlyList<SleepSpan> sleeps, NightWindow window, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(window);
        var classes = new SleepClass[sleeps.Count];
        for (var i = 0; i < sleeps.Count; i++)
        {
            var sleep = sleeps[i];
            var start = Local(sleep.Start, zone);
            var night = sleep.Override switch
            {
                SleepKindOverride.Night => true,
                SleepKindOverride.Nap => false,
                _ => sleep.Length >= NightLength
                     || (sleep.Length >= NightInWindowLength && window.Contains(start))
                     || (i > 0 && classes[i - 1] == SleepClass.Night && sleeps[i - 1].End is { } woke
                         && sleep.Start - woke <= NightWakingAtMost && window.IsNightTime(start)),
            };
            classes[i] = night ? SleepClass.Night : SleepClass.Nap;
        }

        return classes;
    }

    internal static TimeOnly Local(DateTimeOffset at, TimeZoneInfo zone) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    private static int FromNoon(TimeOnly time) => ((time.Hour * 60) + time.Minute + (12 * 60)) % (24 * 60);

    private static TimeOnly ToTime(int fromNoon) => new TimeOnly(12, 0).AddMinutes(fromNoon);
}
