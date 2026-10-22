using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
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

            // Daytime: feed, play, nap, about every three hours; a diaper with most feeds.
            var t = morning.AddMinutes(10);
            var napLength = 60 + (week * 7);
            for (var cycle = 0; cycle < 4; cycle++)
            {
                day.Add(Breast(t, 8 + random.Next(7), 6 + random.Next(7), cycle % 2 == 0));
                day.Add(Diaper(t.AddMinutes(25), cycle is 1 or 3 ? (random.Next(3) == 0 ? DiaperContents.Mixed : DiaperContents.Dirty) : DiaperContents.Wet));
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
