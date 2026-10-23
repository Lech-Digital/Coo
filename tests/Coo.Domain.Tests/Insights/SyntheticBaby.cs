using Coo.Domain.Insights;

namespace Coo.Domain.Tests.Insights;

/// <summary>A seeded generator (ADR 0004 §10: tests are deterministic): the same seed gives the same baby.</summary>
internal sealed class Lcg(ulong seed)
{
    private ulong _state = seed * 6364136223846793005UL + 1442695040888963407UL;

    public double Next()
    {
        _state = (_state * 6364136223846793005UL) + 1442695040888963407UL;
        return (_state >> 11) * (1.0 / (1UL << 53));
    }

    public double Between(double low, double high) => low + ((high - low) * Next());

    /// <summary>Standard normal, by Box–Muller.</summary>
    public double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - Next())) * Math.Cos(2 * Math.PI * Next());
}

/// <summary>What kind of baby and family a synthetic history is.</summary>
/// <param name="Factor">This baby's wake windows against the age prior: 1.1 is a baby who stays awake 10% longer.</param>
/// <param name="NoiseMinutes">Day-to-day spread of each window (one standard deviation).</param>
/// <param name="MissRate">Share of naps nobody logged.</param>
internal sealed record BabyTraits(int AgeDays, int Days, double Factor, double NoiseMinutes, double MissRate, double ShortNapRate, ulong Seed);

/// <summary>
/// Synthetic sleep histories for the backtest (ADR 0008 §6): a baby that grows through the prior's bands, with its own
/// rhythm, noisy days, short naps that shorten the next window, broken nights while young, naps nobody logged, and times
/// rounded to five minutes as people log them. It encodes our own assumptions: real histories must confirm it.
/// </summary>
internal static class SyntheticBaby
{
    public static readonly DateOnly FirstDay = new(2026, 3, 2);

    /// <summary>Every sleep as it happened, and the ones the family logged (rounded, some missing).</summary>
    public static (List<SleepSpan> Truth, List<SleepSpan> Logged) Generate(BabyTraits traits, TimeZoneInfo zone)
    {
        var random = new Lcg(traits.Seed);
        var truth = new List<SleepSpan>();
        var logged = new List<SleepSpan>();
        DateTimeOffset At(DateOnly day, double minutes) =>
            new(day.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes), zone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes)));
        void Log(DateTimeOffset start, DateTimeOffset end, bool miss)
        {
            truth.Add(new SleepSpan(start, end));
            if (!miss)
                logged.Add(new SleepSpan(Round(start), Round(end)));
        }

        var awake = At(FirstDay, 390 + (15 * random.Gaussian()));
        for (var d = 0; d < traits.Days; d++)
        {
            var day = FirstDay.AddDays(d);
            var band = WakeWindowPrior.For(traits.AgeDays + d);
            var shortened = false;
            for (var k = 1; k <= band.TypicalNaps + 1; k++)
            {
                var minutes = (band.At(k).TotalMinutes * traits.Factor) + (traits.NoiseMinutes * random.Gaussian());
                if (shortened)
                    minutes *= 0.85;
                var sleepStart = awake.AddMinutes(Math.Max(15, minutes));
                if (k <= band.TypicalNaps)
                {
                    shortened = random.Next() < traits.ShortNapRate;
                    var length = shortened ? random.Between(25, 40) : random.Between(45, 110);
                    awake = sleepStart.AddMinutes(length);
                    Log(sleepStart, awake, random.Next() < traits.MissRate);
                    continue;
                }

                var morning = At(day.AddDays(1), 390 + (15 * random.Gaussian()));
                if (traits.AgeDays + d < 12 * 7)
                {
                    // A young baby's night: stretches of about three hours, woken for feeds.
                    var from = sleepStart;
                    while (from < morning)
                    {
                        var to = from.AddMinutes(random.Between(150, 210));
                        if (to > morning.AddMinutes(-60))
                            to = morning;
                        Log(from, to, false);
                        from = to.AddMinutes(random.Between(25, 45));
                    }
                }
                else
                {
                    Log(sleepStart, morning, false);
                }

                awake = morning;
            }
        }

        return (truth, logged);
    }

    private static DateTimeOffset Round(DateTimeOffset at)
    {
        var minutes = Math.Round(at.TimeOfDay.TotalMinutes / 5) * 5;
        return new DateTimeOffset(at.Date.AddMinutes(minutes), at.Offset);
    }
}
