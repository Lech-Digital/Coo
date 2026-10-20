using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class FeedAutoMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));

    [Fact]
    public void CanAutoMerge_Left5MinThenPause3MinThenRight10Min_ReturnsTrue()
    {
        // Baby fed Left 5m (10:00 - 10:05), paused 3m, then Right 10m (10:08 - 10:18)
        var left = Breast(Now, 5, BreastSide.Left);
        var right = Breast(Now.AddMinutes(8), 10, BreastSide.Right);

        var outcome = FeedMerge.Plan([left, right]);
        outcome.IsMergeable.Should().BeTrue();

        var group = new AttentionGroup([left, right], [new EntryPair(left.Id, right.Id)], outcome);
        FeedAutoMerge.CanAutoMerge(group).Should().BeTrue();
    }

    [Fact]
    public void CanAutoMerge_FeedsMoreThan15MinApart_ReturnsFalse()
    {
        // Baby fed Left 5m (10:00 - 10:05), paused 20m, then Right 10m (10:25 - 10:35)
        var left = Breast(Now, 5, BreastSide.Left);
        var right = Breast(Now.AddMinutes(25), 10, BreastSide.Right);

        var outcome = FeedMerge.Plan([left, right]);
        var group = new AttentionGroup([left, right], [new EntryPair(left.Id, right.Id)], outcome);

        FeedAutoMerge.CanAutoMerge(group).Should().BeFalse();
    }

    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private long _wall = 1;

    [Fact]
    public void CanAutoResolveDuplicate_IdenticalDiapersWithinTwoMinutes_ReturnsTrue()
    {
        var d1 = Diaper(Now);
        var d2 = Diaper(Now.AddSeconds(30));

        FeedAutoMerge.CanAutoResolveDuplicate(d1, d2).Should().BeTrue();
    }

    private CareEntry Breast(DateTimeOffset start, int minutes, BreastSide side = BreastSide.Left) =>
        Make(BreastFeed.Create([new BreastSegment(side, start, start.AddMinutes(minutes))], isCompleted: true).Value, null);

    private CareEntry Diaper(DateTimeOffset at) =>
        Make(new Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Wet), EntryTiming.Point(at));

    private CareEntry Make(EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))),
            Household,
            EntrySubject.Baby(Baby),
            details,
            timing,
            PersonRef.ThisDevicesPerson,
            EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device),
            Now).Value;
}
