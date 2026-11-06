using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Demo;

/// <summary>
/// The store screenshots' made-up baby (store-assets/PLAN.md §1): Lena, about seven weeks old, with six weeks of a
/// believable rhythm — breast feeds every two to three hours and a bottle in the evening, naps and a lengthening night,
/// wet and dirty diapers, a morning pump — so Today, Log and Stats look lived in. Only the demo build
/// (<c>-p:CooDemo=true</c>, its own app ID) calls this, and only on an empty phone. Nothing in it is anyone's real data.
/// </summary>
public sealed class SeedDemoLog(EnsureProvisionalHousehold households, IHouseholdStore store, ICareLogStore log, IVersionClock clock, TimeProvider time)
{
    public const string BabyName = "Lena";
    public const int Days = 42;

    /// <returns>False when the phone already has a baby.</returns>
    public async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
    {
        var household = await households.ExecuteAsync(cancellationToken);
        if ((await store.ListBabiesAsync(household.Id, cancellationToken)).Count > 0)
            return false;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var now = time.GetUtcNow();
        var today = DayMath.DateOf(now, zone);
        var baby = BabyProfile.Create(
            new BabyId(Guid.CreateVersion7(now)), household.Id, BabyName, [], today.AddDays(-49), null, await clock.NextAsync(cancellationToken), today).Value;
        await store.SaveBabyAsync(baby, cancellationToken);

        var entries = new List<CareEntry>();
        foreach (var plan in Plan(today, zone, now))
        {
            var created = CareEntry.Create(
                new EntryId(Guid.CreateVersion7(plan.At)),
                household.Id,
                plan.Details is PumpDetails ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(baby.Id),
                plan.Details,
                plan.Timing,
                PersonRef.ThisDevicesPerson,
                EntrySource.Manual,
                await clock.NextAsync(cancellationToken),
                now);
            if (created.IsSuccess)
                entries.Add(created.Value);
        }

        await log.WriteAllAsync(new CareLogWrites(entries, [], []), cancellationToken);
        return true;
    }

    /// <summary>Check-ups by days back from today: a healthy girl a little above the WHO median.</summary>
    private static readonly Dictionary<int, (int Grams, int? LengthMm, int? HeadMm)> Measured = new()
    {
        [40] = (3_450, 508, 350),
        [27] = (4_050, 535, 364),
        [13] = (4_620, 558, 376),
        [1] = (5_050, null, null),
    };

    internal sealed record Planned(DateTimeOffset At, EntryDetails Details, EntryTiming? Timing);

    /// <summary>The demo's entries, oldest first, all before <paramref name="now"/>. Seeded, so every run looks the same.</summary>
    internal static IEnumerable<Planned> Plan(DateOnly today, TimeZoneInfo zone, DateTimeOffset now)
    {
        var random = new Dice(49);
        DateTimeOffset At(DateOnly date, double hour) =>
            DayMath.Day(date, zone).Start.AddMinutes(Math.Round(hour * 60));

        for (var back = Days - 1; back >= 0; back--)
        {
            var date = today.AddDays(-back);
            var week = (Days - 1 - back) / 7; // 0 to 5: each week a little more night sleep.
            var day = new List<Planned>();

            // The night that ends this morning: down at ~20:30 the evening before, up at ~6:30, one feed in between.
            var down = At(date.AddDays(-1), 20.6 + (random.NextDouble() * 0.4));
            var wake1 = At(date, 1.4 + (week * 0.3) + (random.NextDouble() * 0.6));
            day.Add(Sleep(down, wake1));
            day.Add(Breast(wake1.AddMinutes(5), 9 + random.Next(5), 7 + random.Next(5), random.Next(2) == 0));
            var back1 = wake1.AddMinutes(45);
            var morning = At(date, 6.2 + (random.NextDouble() * 0.6));
            day.Add(Sleep(back1, morning));
            // Six or more wet diapers a day, as a well-fed seven-week-old has (owner's review, 2026-11-06: with three,
            // Today warned "fewer than 5 wet diapers" in the store screenshots). No draws, so the rest stays as it was.
            day.Add(Diaper(wake1.AddMinutes(30), DiaperContents.Wet));
            day.Add(Diaper(morning.AddMinutes(5), DiaperContents.Wet));

            // Daytime: feed, play, nap, about every three hours; a diaper with most feeds.
            var t = morning.AddMinutes(10);
            var napLength = 60 + (week * 7);
            for (var cycle = 0; cycle < 4; cycle++)
            {
                day.Add(Breast(t, 8 + random.Next(7), 6 + random.Next(7), cycle % 2 == 0));
                // Wet or dirty only, as the diaper form offers (owner, 2026-11-05: no "mixed"). The draw stays, so the
                // rest of the made-up weeks come out as before.
                var dirty = cycle is 1 or 3;
                if (dirty)
                    _ = random.Next(3);
                day.Add(Diaper(t.AddMinutes(25), dirty ? DiaperContents.Dirty : DiaperContents.Wet));
                var napStart = t.AddMinutes(60 + random.Next(30));
                var napEnd = napStart.AddMinutes(napLength + random.Next(-20, 25));
                day.Add(Sleep(napStart, napEnd));
                t = napEnd.AddMinutes(5);
            }

            if (back <= 13)
                day.Add(new Planned(At(date, 7.5), new PumpDetails(PumpYield.OfTotal(Volume.FromMillilitres(60 + (random.Next(7) * 10)).Value)), EntryTiming.Session(TimeRange.Create(At(date, 7.5), At(date, 7.8)).Value)));
            var evening = t > At(date, 18) ? t.AddMinutes(30) : At(date, 18.5);
            day.Add(new Planned(evening, BottleFeed.Of(Milk.ExpressedBreastMilk, Volume.FromMillilitres(70 + (random.Next(4) * 10)).Value).Value, PointOf(evening)));
            day.Add(Diaper(evening.AddMinutes(40), DiaperContents.Wet));
            day.Add(Diaper(evening.AddMinutes(110), DiaperContents.Wet));

            // Vitamin D every morning, and the check-ups' measurements for the growth chart (a visit with each).
            if (back <= 13)
                day.Add(new Planned(At(date, 9), MedicineDetails.Create("Vitamin D", "400 IU").Value, PointOf(At(date, 9))));
            if (Measured.TryGetValue(back, out var measured))
            {
                day.Add(new Planned(At(date, 10.5), GrowthDetails.Create(measured.Grams, measured.LengthMm, measured.HeadMm).Value, PointOf(At(date, 10.5))));
                day.Add(new Planned(At(date, 10.4), VisitDetails.Create("Paediatrician").Value, PointOf(At(date, 10.4))));
            }

            // Only what has happened: today stops at now, and a sleep not over by then is still going.
            foreach (var entry in day.Where(e => e.At < now).OrderBy(e => e.At))
            {
                if (entry.Timing is not SessionTiming { Range: { End: { } end } range } || end <= now)
                    yield return entry;
                else if (entry.Details is SleepDetails)
                    yield return entry with { Timing = EntryTiming.Session(TimeRange.Create(range.Start, null).Value) };
            }
        }
    }

    /// <summary>A small fixed-seed generator (an LCG): <see cref="Random"/> is banned, and the demo must look the same every run.</summary>
    private sealed class Dice(uint seed)
    {
        private uint _state = seed;

        public double NextDouble()
        {
            _state = unchecked((_state * 1664525) + 1013904223);
            return (_state >> 8) / (double)(1 << 24);
        }

        public int Next(int max) => (int)(NextDouble() * max);

        public int Next(int min, int max) => min + Next(max - min);
    }

    private static EntryTiming PointOf(DateTimeOffset at) => EntryTiming.Point(at);

    private static Planned Sleep(DateTimeOffset start, DateTimeOffset end) =>
        new(start, new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private static Planned Diaper(DateTimeOffset at, DiaperContents contents) =>
        new(at, new DiaperDetails(contents), EntryTiming.Point(at));

    private static Planned Breast(DateTimeOffset start, int first, int second, bool leftFirst) =>
        new(start, BreastFeed.FromSideDurations(start, leftFirst
            ? [(BreastSide.Left, first), (BreastSide.Right, second)]
            : [(BreastSide.Right, first), (BreastSide.Left, second)]).Value, null);
}
