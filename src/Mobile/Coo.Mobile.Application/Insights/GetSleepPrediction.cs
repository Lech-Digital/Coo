using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Insights;

/// <summary>
/// The next sleep window for a baby (ADR 0008 §9): the baby's sleeps of the last fortnight from the log, the corrected
/// age, and the predictor. Null for a baby not born yet, while asleep, at night, or before any sleep was logged.
/// </summary>
public sealed class GetSleepPrediction(ICareLogStore store)
{
    /// <summary>A day more than the predictor looks back, so a night that began before the fortnight still counts.</summary>
    private static readonly TimeSpan Reach = PredictorParameters.Default.LookBack + TimeSpan.FromDays(1);

    public async Task<SleepPrediction?> ExecuteAsync(
        HouseholdId household, BabyProfile baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(zone);
        if (!baby.IsBorn)
            return null;

        var sleeps = await SleepsAsync(household, baby.Id, now - Reach, now, cancellationToken);

        var age = BabyAge.On(baby, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime));
        return SleepPredictor.Predict(sleeps, age.CorrectedDays, now, zone);
    }

    /// <summary>
    /// How the shown windows did for this baby over the last fortnight, replayed on the phone from her own log (proving
    /// the predictor, owner 2026-11-03); null for a baby not born yet.
    /// </summary>
    public async Task<PredictionTrackRecord?> TrackRecordAsync(
        HouseholdId household, BabyProfile baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(zone);
        if (!baby.IsBorn)
            return null;

        var sleeps = await SleepsAsync(household, baby.Id, now - SleepPredictionCheck.Over - Reach, now, cancellationToken);
        var age = BabyAge.On(baby, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime));
        return SleepPredictionCheck.Replay(sleeps, age.CorrectedDays, now, zone);
    }

    private async Task<List<SleepSpan>> SleepsAsync(
        HouseholdId household, BabyId baby, DateTimeOffset from, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await store.ListAsync(household, from, now.AddMinutes(1), cancellationToken);
        return [.. stored
            .OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => GetToday.IsAbout(e, baby))
            .Select(e => e is { Details: SleepDetails details, Timing: SessionTiming timing }
                ? new SleepSpan(timing.Range.Start, timing.Range.End, details.KindOverride)
                : null)
            .OfType<SleepSpan>()];
    }
}
