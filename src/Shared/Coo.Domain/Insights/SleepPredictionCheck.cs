namespace Coo.Domain.Insights;

/// <summary>
/// How the shown nap windows did for this baby lately: of <see cref="Shown"/> windows, <see cref="Hits"/> had the sleep
/// start inside them, and the typical distance from the window's centre.
/// </summary>
public sealed record PredictionTrackRecord(int Shown, int Hits, TimeSpan MedianError)
{
    /// <summary>Fewer windows than this say nothing yet, so none is shown.</summary>
    public const int MinimumShown = 5;

    public bool IsEnough => Shown >= MinimumShown;

    public double HitRate => Shown == 0 ? 0 : Hits / (double)Shown;
}

/// <summary>
/// Proving the predictor on the baby's own log (owner, 2026-11-03): replays the predictor at each wake-up of the last
/// <see cref="Over"/>, from only the sleeps logged before it, as the phone would have, and checks the window against when
/// she really fell asleep. On the phone, from the log it already holds; nothing is sent (the privacy policy promises no
/// analytics). Only the windows Today would have shown as hers count.
/// </summary>
public static class SleepPredictionCheck
{
    public static readonly TimeSpan Over = TimeSpan.FromDays(14);

    /// <summary>The prediction is made a few minutes after waking, when the parent has logged it.</summary>
    public static readonly TimeSpan AfterWaking = TimeSpan.FromMinutes(5);

    /// <param name="sleeps">The baby's sleeps of at least <see cref="Over"/> plus the predictor's look-back, any order.</param>
    /// <param name="correctedAgeDaysNow">Her corrected age today; the replays use her age on their day.</param>
    public static PredictionTrackRecord Replay(
        IEnumerable<SleepSpan> sleeps, int correctedAgeDaysNow, DateTimeOffset now, TimeZoneInfo zone, PredictorParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(zone);
        var ended = sleeps.Where(s => s.End is { } end && end <= now).OrderBy(s => s.Start).ToList();
        var errors = new List<TimeSpan>();
        var hits = 0;
        for (var j = 1; j < ended.Count; j++)
        {
            var next = ended[j];
            var at = ended[j - 1].End!.Value + AfterWaking;
            if (next.Start < now - Over || next.Start <= at)
                continue;

            var history = ended.Take(j).ToList();
            var age = correctedAgeDaysNow - (int)Math.Floor((now - at).TotalDays);
            if (SleepPredictor.Predict(history, Math.Max(0, age), at, zone, parameters) is not { IsPersonal: true } shown)
                continue;

            // A window long past when she slept is a gap in the log, not a miss: nobody logged the sleep between.
            if (next.Start - shown.Latest > TimeSpan.FromHours(2))
                continue;

            errors.Add((next.Start - shown.Centre).Duration());
            if (next.Start >= shown.Earliest && next.Start <= shown.Latest)
                hits++;
        }

        errors.Sort();
        return new PredictionTrackRecord(errors.Count, hits, errors.Count == 0 ? TimeSpan.Zero : errors[errors.Count / 2]);
    }
}
