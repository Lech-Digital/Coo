using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Plausibility;

namespace Coo.Domain.Tests.CareLog;

public sealed class FeedingTests
{
    private static readonly DateTimeOffset T0 = A.Now.AddHours(-2);

    [Fact]
    public void SwitchSide_WhileRunningLeft_ClosesLeftAndOpensRight()
    {
        // Arrange
        var feed = BreastFeed.Start(BreastSide.Left, T0);

        // Act
        var switched = feed.SwitchSide(T0.AddMinutes(8)).Value;

        // Assert
        switched.Segments.Should().Equal(
            new BreastSegment(BreastSide.Left, T0, T0.AddMinutes(8)),
            new BreastSegment(BreastSide.Right, T0.AddMinutes(8), null));
        switched.LeftDuration.Should().Be(TimeSpan.FromMinutes(8));
    }

    [Fact]
    public void Complete_WhilePaused_MarksCompletedWithoutNewSegment()
    {
        // Arrange
        var paused = BreastFeed.Start(BreastSide.Left, T0).Pause(T0.AddMinutes(10)).Value;

        // Act
        var completed = paused.Complete(T0.AddMinutes(30)).Value;

        // Assert
        completed.IsCompleted.Should().BeTrue();
        completed.Segments.Should().ContainSingle();
        completed.TotalDuration.Should().Be(TimeSpan.FromMinutes(10), "a pause is not feeding time");
    }

    [Fact]
    public void FromSideDurations_LeftTenRightFive_BuildsBackToBackSegments()
    {
        // Arrange
        (BreastSide, int)[] sides = [(BreastSide.Left, 10), (BreastSide.Right, 5)];

        // Act
        var feed = BreastFeed.FromSideDurations(T0, sides).Value;

        // Assert
        feed.Segments.Should().Equal(
            new BreastSegment(BreastSide.Left, T0, T0.AddMinutes(10)),
            new BreastSegment(BreastSide.Right, T0.AddMinutes(10), T0.AddMinutes(15)));
        feed.IsCompleted.Should().BeTrue();
        feed.LastSide.Should().Be(BreastSide.Right);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public void FromSideDurations_SideOutsideOneTo240Minutes_FailsWithSideDurationInvalid(int minutes)
    {
        // Arrange
        (BreastSide, int)[] sides = [(BreastSide.Left, minutes)];

        // Act
        var result = BreastFeed.FromSideDurations(T0, sides);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BreastSideDurationInvalid);
    }

    [Fact]
    public void FromSideDurations_NoSides_FailsWithNoSegments()
    {
        // Arrange
        (BreastSide, int)[] sides = [];

        // Act
        var result = BreastFeed.FromSideDurations(T0, sides);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BreastNoSegments);
    }

    [Fact]
    public void Create_OverlappingSegments_FailsWithSegmentsOverlap()
    {
        // Arrange
        BreastSegment[] segments =
        [
            new(BreastSide.Left, T0, T0.AddMinutes(10)),
            new(BreastSide.Right, T0.AddMinutes(5), T0.AddMinutes(12)),
        ];

        // Act
        var result = BreastFeed.Create(segments, isCompleted: true);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BreastSegmentsOverlap);
    }

    [Fact]
    public void Create_OpenSegmentBeforeAnother_FailsWithOpenSegmentNotLast()
    {
        // Arrange
        BreastSegment[] segments = [new(BreastSide.Left, T0, null), new(BreastSide.Right, T0.AddMinutes(5), null)];

        // Act
        var result = BreastFeed.Create(segments, isCompleted: false);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BreastOpenSegmentNotLast);
    }

    [Fact]
    public void Create_CompletedWithOpenSegment_FailsWithCompletedWithOpenSegment()
    {
        // Arrange
        BreastSegment[] segments = [new(BreastSide.Left, T0, null)];

        // Act
        var result = BreastFeed.Create(segments, isCompleted: true);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BreastCompletedWithOpenSegment);
    }

    [Fact]
    public void Complete_SpanExactlySixHours_Succeeds()
    {
        // Arrange
        var feed = BreastFeed.Start(BreastSide.Left, T0);

        // Act
        var result = feed.Complete(T0.AddHours(6));

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Complete_SpanOverSixHours_FailsWithTooLong()
    {
        // Arrange
        var feed = BreastFeed.Start(BreastSide.Left, T0);

        // Act
        var result = feed.Complete(T0.AddHours(6).AddMinutes(1));

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BreastTooLong);
    }

    [Fact]
    public void Resume_WhileSegmentOpen_FailsWithSegmentAlreadyOpen()
    {
        // Arrange
        var feed = BreastFeed.Start(BreastSide.Left, T0);

        // Act
        var result = feed.Resume(BreastSide.Right, T0.AddMinutes(1));

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BreastSegmentAlreadyOpen);
    }

    [Fact]
    public void Create_BreastFeed_TimingIsSessionSpanningSegments()
    {
        // Arrange
        var start = T0;

        // Act
        var entry = A.Breast(start, leftMinutes: 10, rightMinutes: 5);

        // Assert
        var session = entry.Timing.Should().BeOfType<SessionTiming>().Subject;
        session.Range.Start.Should().Be(start);
        session.Range.End.Should().Be(start.AddMinutes(15));
    }

    [Fact]
    public void Revise_SwitchSideOnRunningTimer_KeepsSessionOpen()
    {
        // Arrange
        var entry = CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(), BreastFeed.Start(BreastSide.Left, T0),
            timing: null, PersonRef.User(A.Parent), EntrySource.Timer, A.Stamp(), A.Now).Value;

        // Act
        var result = entry.Revise<BreastFeed>(d => d.SwitchSide(T0.AddMinutes(7)), A.Stamp(), A.Now);

        // Assert
        result.IsSuccess.Should().BeTrue();
        entry.Timing.IsInProgress.Should().BeTrue();
        ((BreastFeed)entry.Details).LastSide.Should().Be(BreastSide.Right);
    }

    [Fact]
    public void Revise_BreastToBottle_FailsWithMethodChangeNotAllowed()
    {
        // Arrange
        var entry = A.Breast(T0);
        var bottle = BottleFeed.Of(Milk.Formula, A.Ml(120)).Value;

        // Act
        var result = entry.Revise<FeedingDetails>(_ => bottle, A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.MethodChangeNotAllowed);
        entry.Details.Should().BeOfType<BreastFeed>("a failed revision changes nothing");
    }

    [Fact]
    public void Create_MixedBottle_TotalIsSumOfComponents()
    {
        // Arrange
        BottleComponent[] components = [new(Milk.ExpressedBreastMilk, A.Ml(60)), new(Milk.Formula, A.Ml(90))];

        // Act
        var bottle = BottleFeed.Create(components).Value;

        // Assert
        bottle.Total.Should().Be(A.Ml(150));
    }

    [Fact]
    public void Create_BottleWithFormulaTwice_FailsWithDuplicateMilk()
    {
        // Arrange
        BottleComponent[] components = [new(Milk.Formula, A.Ml(60)), new(Milk.Formula, A.Ml(30))];

        // Act
        var result = BottleFeed.Create(components);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BottleDuplicateMilk);
    }

    [Fact]
    public void Create_BottleTotalOver1000Ml_FailsWithTotalOutOfRange()
    {
        // Arrange
        BottleComponent[] components = [new(Milk.ExpressedBreastMilk, A.Ml(600)), new(Milk.Formula, A.Ml(401))];

        // Act
        var result = BottleFeed.Create(components);

        // Assert
        result.Error!.Code.Should().Be(FeedingErrors.BottleTotalOutOfRange);
    }

    [Theory]
    [InlineData(4.9, true)]
    [InlineData(5.0, false)]
    [InlineData(400.0, false)]
    [InlineData(400.1, true)]
    public void For_BottleAmount_WarnsOutside5To400Ml(double ml, bool warns)
    {
        // Arrange
        var entry = A.Bottle(A.Now.AddMinutes(-5), (decimal)ml);

        // Act
        var warnings = PlausibilityCheck.For(entry, new PlausibilityContext(A.Now));

        // Assert
        warnings.Contains(PlausibilityCodes.BottleAmount).Should().Be(warns);
    }
    [Fact]
    public void BreastSegment_InstantsWithTicksBelowTheMillisecond_AreHeldToTheMillisecond()
    {
        // Arrange: a phone clock has ticks below the millisecond; every store and the wire keep milliseconds.
        var start = T0.AddTicks(1_234_567);
        var end = T0.AddMinutes(10).AddTicks(7_654);

        // Act
        var segment = new BreastSegment(BreastSide.Left, start, end);
        var moved = segment with { Start = start.AddTicks(4_321) };

        // Assert
        segment.Start.Should().Be(T0.AddMilliseconds(123));
        segment.Start.Offset.Should().Be(T0.Offset);
        segment.End.Should().Be(T0.AddMinutes(10));
        moved.Start.Should().Be(T0.AddMilliseconds(123));
    }

    [Fact]
    public void Complete_TimerOnASubMillisecondClock_DerivesTheTimingTheStoredColumnsHold()
    {
        // Arrange
        var start = T0.AddTicks(1_234_567);
        var feed = BreastFeed.Start(BreastSide.Left, start).SwitchSide(start.AddMinutes(8).AddTicks(1)).Value;

        // Act
        var stop = start.AddMinutes(15).AddTicks(9_999);
        var completed = feed.Complete(stop).Value;

        // Assert
        var range = ((SessionTiming)FeedingKind.Instance.DeriveTiming(completed)).Range;
        range.Start.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(start.ToUnixTimeMilliseconds()));
        range.End.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(stop.ToUnixTimeMilliseconds()));
        completed.Segments.Should().OnlyContain(s => s.Start.Ticks % TimeSpan.TicksPerMillisecond == 0
            && (s.End == null || s.End.Value.Ticks % TimeSpan.TicksPerMillisecond == 0));
    }
}
