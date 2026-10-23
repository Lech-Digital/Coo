using Coo.Domain.Insights;
using Xunit.Abstractions;

namespace Coo.Domain.Tests.Insights;

/// <summary>
/// The predictor's acceptance test (ADR 0008 §6): replays synthetic histories, predicts each sleep from only what came
/// before it, and measures the window against the real start. High confidence must reach a mean absolute error of 20
/// minutes or less and a hit rate of 60% or more, and the predictor must beat the age prior alone.
/// </summary>
public sealed class SleepPredictorBacktest(ITestOutputHelper output)
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    /// <summary>Babies across the bands: newborn to toddler, calm and erratic, careful and patchy logging.</summary>
    internal static readonly BabyTraits[] Babies =
    [
        new(AgeDays: 10, Days: 28, Factor: 1.0, NoiseMinutes: 8, MissRate: 0.05, ShortNapRate: 0.15, Seed: 1),
        new(AgeDays: 45, Days: 28, Factor: 1.1, NoiseMinutes: 10, MissRate: 0.05, ShortNapRate: 0.2, Seed: 2),
        new(AgeDays: 100, Days: 28, Factor: 0.9, NoiseMinutes: 12, MissRate: 0.1, ShortNapRate: 0.15, Seed: 3),
        new(AgeDays: 160, Days: 28, Factor: 1.05, NoiseMinutes: 12, MissRate: 0.05, ShortNapRate: 0.1, Seed: 4),
        new(AgeDays: 230, Days: 28, Factor: 0.95, NoiseMinutes: 15, MissRate: 0.05, ShortNapRate: 0.1, Seed: 5),
        new(AgeDays: 300, Days: 28, Factor: 1.15, NoiseMinutes: 12, MissRate: 0.1, ShortNapRate: 0.1, Seed: 6),
        new(AgeDays: 400, Days: 28, Factor: 1.0, NoiseMinutes: 15, MissRate: 0.05, ShortNapRate: 0.05, Seed: 7),
        new(AgeDays: 550, Days: 28, Factor: 0.9, NoiseMinutes: 15, MissRate: 0.05, ShortNapRate: 0.05, Seed: 8),
    ];

    [Fact]
    public void Backtest_SyntheticBabies_HighConfidenceMeetsTheReleaseTargets() => MeetsTargets(Babies);

    /// <summary>The same babies with other seeds: the parameters were tuned on the first set, so they must hold on these.</summary>
    [Fact]
    public void Backtest_FreshSeeds_HighConfidenceMeetsTheReleaseTargets() =>
        MeetsTargets([.. Babies.Select(b => b with { Seed = b.Seed + 100 })]);

    private void MeetsTargets(IReadOnlyList<BabyTraits> babies)
    {
        // Arrange
        var results = babies.SelectMany(b => Replay(b, Amsterdam)).ToList();
        var high = results.Where(r => r.Confidence == PredictionConfidence.High).ToList();

        // Act
        var highError = high.Average(r => r.Error);
        var highHits = high.Count(r => r.Hit) / (double)high.Count;
        var allError = results.Average(r => r.Error);
        var priorError = results.Average(r => r.PriorError);
        output.WriteLine($"{results.Count} predictions, {high.Count} high confidence");
        output.WriteLine($"High: mean error {highError:0.0} min, hit rate {highHits:P0}");
        output.WriteLine($"All: mean error {allError:0.0} min, age prior alone {priorError:0.0} min");

        // Assert
        high.Count.Should().BeGreaterThan(results.Count / 2, "a week of steady logging should earn high confidence");
        highError.Should().BeLessThanOrEqualTo(20);
        highHits.Should().BeGreaterThanOrEqualTo(0.6);
        allError.Should().BeLessThan(priorError);
    }

    internal sealed record Outcome(PredictionConfidence Confidence, double Error, bool Hit, double PriorError);

    /// <summary>
    /// For each real sleep, predicts from the logged sleeps before it, as the phone would just after the baby woke. A sleep
    /// whose previous one nobody logged is skipped: the phone cannot know when the baby woke.
    /// </summary>
    internal static IEnumerable<Outcome> Replay(BabyTraits baby, TimeZoneInfo zone, PredictorParameters? parameters = null)
    {
        var (truth, logged) = SyntheticBaby.Generate(baby, zone);
        var start = truth[0].Start.AddDays(3);
        for (var j = 1; j < truth.Count; j++)
        {
            var next = truth[j];
            var history = logged.Where(s => s.Start < next.Start).ToList();
            if (next.Start < start || history.Count == 0 || Math.Abs((history[^1].End!.Value - truth[j - 1].End!.Value).TotalMinutes) > 5)
                continue;

            var now = history[^1].End!.Value.AddMinutes(5);
            if (next.Start <= now)
                continue;

            var age = baby.AgeDays + (DateOnly.FromDateTime(now.DateTime).DayNumber - SyntheticBaby.FirstDay.DayNumber);
            var prediction = SleepPredictor.Predict(history, age, now, zone, parameters);
            if (prediction is null)
                continue;

            var prior = prediction.AwakeSince + prediction.Prior.At(prediction.Position);
            yield return new Outcome(
                prediction.Confidence,
                Math.Abs((next.Start - prediction.Centre).TotalMinutes),
                next.Start >= prediction.Earliest && next.Start <= prediction.Latest,
                Math.Abs((next.Start - prior).TotalMinutes));
        }
    }
}
