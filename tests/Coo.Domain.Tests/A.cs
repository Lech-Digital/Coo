using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.Tests;

/// <summary>Test data builders. Domain objects are built for real, never mocked.</summary>
internal static class A
{
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 14, 0, 0, TimeSpan.FromHours(2));
    public static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-000000000001"));
    public static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    public static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    public static readonly BabyId OtherBaby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2"));
    public static readonly UserId Parent = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    public static readonly UserId OtherParent = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));

    private static long _wallClock = 1_790_000_000_000;
    private static int _ids;

    /// <summary>A stamp greater than every stamp issued before it.</summary>
    public static HlcStamp Stamp() => new(Interlocked.Increment(ref _wallClock), 0, Device);

    public static EntryId EntryId() =>
        new(Guid.CreateVersion7(Now.AddMilliseconds(Interlocked.Increment(ref _ids))));

    public static EntrySubject BabySubject(BabyId? baby = null) => EntrySubject.Baby(baby ?? Baby);

    public static CareEntry Diaper(DateTimeOffset at, BabyId? baby = null, DiaperContents contents = DiaperContents.Wet) =>
        CareEntry.Create(EntryId(), Household, BabySubject(baby), new DiaperDetails(contents), EntryTiming.Point(at),
            PersonRef.User(Parent), EntrySource.Manual, Stamp(), Now).Value;

    public static CareEntry Sleep(DateTimeOffset start, DateTimeOffset? end, BabyId? baby = null) =>
        CareEntry.Create(EntryId(), Household, BabySubject(baby), new SleepDetails(),
            EntryTiming.Session(TimeRange.Create(start, end).Value), PersonRef.User(Parent), EntrySource.Manual, Stamp(), Now).Value;

    public static CareEntry Bottle(DateTimeOffset at, decimal ml = 120m, Milk milk = Milk.Formula, BabyId? baby = null) =>
        CareEntry.Create(EntryId(), Household, BabySubject(baby), BottleFeed.Of(milk, Volume.FromMillilitres(ml).Value).Value,
            EntryTiming.Point(at), PersonRef.User(Parent), EntrySource.Manual, Stamp(), Now).Value;

    public static CareEntry Breast(DateTimeOffset start, int leftMinutes = 10, int rightMinutes = 5, BabyId? baby = null) =>
        CareEntry.Create(EntryId(), Household, BabySubject(baby),
            BreastFeed.FromSideDurations(start, [(BreastSide.Left, leftMinutes), (BreastSide.Right, rightMinutes)]).Value,
            timing: null, PersonRef.User(Parent), EntrySource.Manual, Stamp(), Now).Value;

    public static CareEntry Pump(DateTimeOffset start, DateTimeOffset? end, UserId? pumper = null, PumpYield? yield = null) =>
        CareEntry.Create(EntryId(), Household, EntrySubject.Caregiver(PersonRef.User(pumper ?? Parent)), new PumpDetails(yield),
            EntryTiming.Session(TimeRange.Create(start, end).Value), PersonRef.User(Parent), EntrySource.Manual, Stamp(), Now).Value;

    public static Volume Ml(decimal ml) => Volume.FromMillilitres(ml).Value;
}
