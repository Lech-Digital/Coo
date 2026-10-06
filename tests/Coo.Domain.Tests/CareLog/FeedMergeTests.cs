using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;

namespace Coo.Domain.Tests.CareLog;

public sealed class FeedMergeTests
{
    private static readonly DateTimeOffset T0 = A.Now.AddHours(-5);

    private static CareEntry Breast(DateTimeOffset start, int minutes, BreastSide side = BreastSide.Left, BabyId? baby = null) =>
        CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(baby),
            BreastFeed.FromSideDurations(start, [(side, minutes)]).Value,
            timing: null, PersonRef.User(A.Parent), EntrySource.Manual, A.Stamp(), A.Now).Value;

    private static CareEntry WithNote(CareEntry entry, string text)
    {
        entry.SetNote(Note.From(text).Value, A.Stamp());
        return entry;
    }

    private static FeedMergePlan Planned(params CareEntry[] feeds) => FeedMerge.Plan(feeds).Plan!;

    // ---- Parts ----

    [Theory]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    public void Parts_GapOfSixtyVersusSixtyOneSeconds_JoinsOrSplits(int gapSeconds, int expected)
    {
        // Arrange
        var first = new BreastSegment(BreastSide.Left, T0, T0.AddMinutes(5));
        var second = new BreastSegment(BreastSide.Left, T0.AddMinutes(5).AddSeconds(gapSeconds), T0.AddMinutes(9));
        var feed = BreastFeed.Create([first, second], isCompleted: true).Value;

        // Act
        var parts = feed.Parts;

        // Assert
        parts.Should().Be(expected);
    }

    [Fact]
    public void Parts_SideSwitchBackToBack_IsOnePart()
    {
        // Arrange
        var feed = BreastFeed.FromSideDurations(T0, [(BreastSide.Left, 10), (BreastSide.Right, 8)]).Value;

        // Act
        var parts = feed.Parts;

        // Assert
        parts.Should().Be(1);
    }

    [Fact]
    public void Parts_ThreeRunsSeparatedByPauses_IsThree()
    {
        // Arrange
        var feed = BreastFeed.Create(
        [
            new BreastSegment(BreastSide.Left, T0, T0.AddMinutes(5)),
            new BreastSegment(BreastSide.Right, T0.AddMinutes(5), T0.AddMinutes(8)),
            new BreastSegment(BreastSide.Right, T0.AddMinutes(12), T0.AddMinutes(15)),
            new BreastSegment(BreastSide.Left, T0.AddMinutes(20), T0.AddMinutes(22)),
        ], isCompleted: true).Value;

        // Act
        var parts = feed.Parts;

        // Assert
        parts.Should().Be(3);
    }

    // ---- Plan: breast ----

    [Fact]
    public void Plan_TwoBreastFeeds_ConcatenatesSegmentsAndSumsDuration()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(12), 5, BreastSide.Right);

        // Act
        var plan = Planned(b, a);

        // Assert
        plan.SurvivorId.Should().Be(a.Id);
        plan.DeletedIds.Should().Equal(b.Id);
        var merged = plan.Details.Should().BeOfType<BreastFeed>().Subject;
        merged.Segments.Should().Equal(((BreastFeed)a.Details).Segments.Concat(((BreastFeed)b.Details).Segments));
        merged.TotalDuration.Should().Be(TimeSpan.FromMinutes(15));
        merged.Span.Should().Be(TimeSpan.FromMinutes(17));
        merged.IsCompleted.Should().BeTrue();
        merged.Parts.Should().Be(2);
    }

    [Fact]
    public void Plan_ThreeBreastFeedsChainedWithinFifteenMinutes_MergesInOrder()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(10 + 12), 5);
        var c = Breast(T0.AddMinutes(10 + 12 + 5 + 14), 4, BreastSide.Right);

        // Act
        var plan = Planned(c, a, b);

        // Assert
        plan.SurvivorId.Should().Be(a.Id);
        plan.DeletedIds.Should().BeEquivalentTo([b.Id, c.Id]);
        var merged = (BreastFeed)plan.Details;
        merged.TotalDuration.Should().Be(TimeSpan.FromMinutes(19));
        merged.FirstStart.Should().Be(T0);
        merged.Segments[^1].End.Should().Be(c.Timing.Instants.Last());
        merged.Parts.Should().Be(3);
    }

    [Fact]
    public void Plan_MergedDetails_ReviseTheEarliestEntryWithTimingFollowing()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(12), 5);
        var plan = Planned(a, b);

        // Act
        var result = a.Revise<BreastFeed>(_ => Result<BreastFeed>.Ok((BreastFeed)plan.Details), A.Stamp(), A.Now);

        // Assert
        result.IsSuccess.Should().BeTrue();
        a.Timing.Instants.Last().Should().Be(T0.AddMinutes(17));
    }

    [Fact]
    public void Plan_NotesOnSome_JoinedNonEmptyEarliestFirst()
    {
        // Arrange
        var a = WithNote(Breast(T0, 10), "burped");
        var b = Breast(T0.AddMinutes(12), 5);
        var c = WithNote(Breast(T0.AddMinutes(30), 5), "sleepy");

        // Act
        var plan = Planned(c, b, a);

        // Assert
        plan.Note!.Text.Should().Be("burped · sleepy");
    }

    [Fact]
    public void Plan_NoNotes_NoteIsNull()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(12), 5);

        // Act
        var plan = Planned(a, b);

        // Assert
        plan.Note.Should().BeNull();
    }

    [Fact]
    public void Plan_NotesTooLongWhenJoined_RefusesNoteTooLong()
    {
        // Arrange
        var a = WithNote(Breast(T0, 10), new string('a', 300));
        var b = WithNote(Breast(T0.AddMinutes(12), 5), new string('b', 300));

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.NoteTooLong);
    }

    [Fact]
    public void Plan_OverlappingBreastFeeds_RefusesOverlap()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(5), 10);

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Plan.Should().BeNull();
        outcome.Refusal.Should().Be(FeedMergeRefusal.Overlap);
    }

    [Fact]
    public void Plan_FeedInsideALongerOneThenAFarOne_RefusesOverlap()
    {
        // Arrange
        var a = Breast(T0, 60);
        var b = Breast(T0.AddMinutes(10), 5);
        var c = Breast(T0.AddMinutes(70), 5);

        // Act
        var outcome = FeedMerge.Plan([a, b, c]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.Overlap);
    }

    [Fact]
    public void Plan_GapOverFifteenMinutes_RefusesNotClose()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(10 + 16), 5);

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.NotClose);
    }

    [Fact]
    public void Plan_GapOfExactlyFifteenMinutes_Merges()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(10 + 15), 5);

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Plan.Should().NotBeNull();
    }

    [Fact]
    public void Plan_SpanOverSixHours_RefusesTooLong()
    {
        // Arrange: each feed is fine, chained 14 min apart, the whole span is over 6 h.
        var feeds = new List<CareEntry>();
        var start = T0.AddHours(-2);
        for (var i = 0; i < 25; i++)
        {
            feeds.Add(Breast(start, 1));
            start = start.AddMinutes(15);
        }

        // Act
        var outcome = FeedMerge.Plan(feeds);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.TooLong);
    }

    [Fact]
    public void Plan_ARunningBreastFeed_RefusesRunning()
    {
        // Arrange
        var running = CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(), BreastFeed.Start(BreastSide.Left, T0.AddMinutes(12)),
            timing: null, PersonRef.User(A.Parent), EntrySource.Manual, A.Stamp(), A.Now).Value;

        // Act
        var outcome = FeedMerge.Plan([Breast(T0, 10), running]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.Running);
    }

    [Fact]
    public void Plan_BreastAndBottle_RefusesMixedVariant()
    {
        // Arrange
        var breast = Breast(T0, 10);
        var bottle = A.Bottle(T0.AddMinutes(12));

        // Act
        var outcome = FeedMerge.Plan([breast, bottle]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.MixedVariant);
    }

    [Fact]
    public void Plan_DifferentBabies_RefusesDifferentBaby()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(12), 5, baby: A.OtherBaby);

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.DifferentBaby);
    }

    [Fact]
    public void Plan_AReadOnlyFeed_RefusesReadOnly()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(12), 5);

        // Act
        var outcome = FeedMerge.Plan([a, b], isReadOnly: e => e.Id == b.Id);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.ReadOnly);
    }

    [Fact]
    public void Plan_ADeletedFeed_RefusesReadOnly()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(12), 5);
        b.Delete(A.Stamp());

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.ReadOnly);
    }

    [Fact]
    public void Plan_FewerThanTwoFeeds_Throws()
    {
        // Arrange
        var a = Breast(T0, 10);

        // Act
        var act = () => FeedMerge.Plan([a]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Plan_NonFeedingEntry_RefusesMixedVariant()
    {
        // Arrange
        var a = Breast(T0, 10);
        var diaper = A.Diaper(T0.AddMinutes(12));

        // Act
        var outcome = FeedMerge.Plan([a, diaper]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.MixedVariant);
    }

    // ---- Plan: bottle ----

    [Fact]
    public void Plan_TwoBottlesSameMilk_SumsAmountAtEarliestTime()
    {
        // Arrange
        var a = A.Bottle(T0, 60m);
        var b = A.Bottle(T0.AddMinutes(10), 40m);

        // Act
        var plan = Planned(b, a);

        // Assert
        plan.SurvivorId.Should().Be(a.Id);
        plan.DeletedIds.Should().Equal(b.Id);
        var merged = (BottleFeed)plan.Details;
        merged.Components.Should().Equal(new BottleComponent(Milk.Formula, A.Ml(100m)));
        merged.Total.Millilitres.Should().Be(100m);
    }

    [Fact]
    public void Plan_BottlesOfDifferentMilks_SumsPerMilk()
    {
        // Arrange
        var a = A.Bottle(T0, 60m, Milk.Formula);
        var b = A.Bottle(T0.AddMinutes(5), 30m, Milk.ExpressedBreastMilk);
        var c = A.Bottle(T0.AddMinutes(9), 20m, Milk.Formula);

        // Act
        var plan = Planned(a, b, c);

        // Assert
        var merged = (BottleFeed)plan.Details;
        merged.Components.Should().Equal(
            new BottleComponent(Milk.Formula, A.Ml(80m)),
            new BottleComponent(Milk.ExpressedBreastMilk, A.Ml(30m)));
    }

    [Fact]
    public void Plan_BottlesOverOneThousandMl_RefusesTooMuch()
    {
        // Arrange
        var a = A.Bottle(T0, 600m);
        var b = A.Bottle(T0.AddMinutes(5), 401m);

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.TooMuch);
    }

    [Fact]
    public void Plan_BottlesTotallingExactlyOneThousandMl_Merges()
    {
        // Arrange
        var a = A.Bottle(T0, 600m);
        var b = A.Bottle(T0.AddMinutes(5), 400m);

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Plan.Should().NotBeNull();
    }

    [Fact]
    public void Plan_BottlesMoreThanFifteenMinutesApart_RefusesNotClose()
    {
        // Arrange
        var a = A.Bottle(T0, 60m);
        var b = A.Bottle(T0.AddMinutes(16), 60m);

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.NotClose);
    }

    [Fact]
    public void Plan_BottlesChained_MergesAcrossMoreThanFifteenMinutesInTotal()
    {
        // Arrange
        var a = A.Bottle(T0, 30m);
        var b = A.Bottle(T0.AddMinutes(12), 30m);
        var c = A.Bottle(T0.AddMinutes(24), 30m);

        // Act
        var plan = Planned(a, b, c);

        // Assert
        plan.DeletedIds.Should().BeEquivalentTo([b.Id, c.Id]);
        ((BottleFeed)plan.Details).Total.Millilitres.Should().Be(90m);
    }

    [Fact]
    public void Plan_OverlappingFeedsWithLongNotes_RefusesOverlapNotNoteTooLong()
    {
        // Arrange
        var a = WithNote(Breast(T0, 10), new string('a', 300));
        var b = WithNote(Breast(T0.AddMinutes(5), 10), new string('b', 300));

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.Overlap);
    }

    [Fact]
    public void Plan_BottlesWithNotesTooLongWhenJoined_RefusesNoteTooLong()
    {
        // Arrange
        var a = WithNote(A.Bottle(T0), new string('a', 300));
        var b = WithNote(A.Bottle(T0.AddMinutes(5)), new string('b', 300));

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.NoteTooLong);
    }

    [Fact]
    public void Plan_TooMuchBottlesWithLongNotes_RefusesTooMuchNotNoteTooLong()
    {
        // Arrange
        var a = WithNote(A.Bottle(T0, 600m), new string('a', 300));
        var b = WithNote(A.Bottle(T0.AddMinutes(5), 500m), new string('b', 300));

        // Act
        var outcome = FeedMerge.Plan([a, b]);

        // Assert
        outcome.Refusal.Should().Be(FeedMergeRefusal.TooMuch);
    }

    [Fact]
    public void Plan_TheSameFeedTwice_Throws()
    {
        // Arrange
        var a = A.Bottle(T0);

        // Act
        var act = () => FeedMerge.Plan([a, a]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Plan_BottlesAtTheSameTime_PicksTheSameSurvivorInEitherInputOrder()
    {
        // Arrange
        var a = A.Bottle(T0, 50m);
        var b = A.Bottle(T0, 50m);
        var lowest = a.Id.Value.CompareTo(b.Id.Value) < 0 ? a : b;

        // Act
        var forward = Planned(a, b);
        var reversed = Planned(b, a);

        // Assert
        forward.SurvivorId.Should().Be(lowest.Id);
        reversed.SurvivorId.Should().Be(lowest.Id);
    }

    [Fact]
    public void Plan_BreastFeedsGivenLatestFirst_MergesInStartOrder()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(12), 5);
        var c = Breast(T0.AddMinutes(20), 5);

        // Act
        var plan = Planned(c, b, a);

        // Assert
        plan.SurvivorId.Should().Be(a.Id);
        ((BreastFeed)plan.Details).Segments.Select(s => s.Start).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Parts_SingleSegment_IsOne()
    {
        // Arrange
        var feed = BreastFeed.FromSideDurations(T0, [(BreastSide.Left, 5)]).Value;

        // Act
        var parts = feed.Parts;

        // Assert
        parts.Should().Be(1);
    }

    [Fact]
    public void Parts_OpenLastSegmentAfterAPause_CountsItAsAPart()
    {
        // Arrange
        var feed = BreastFeed.Start(BreastSide.Left, T0).Pause(T0.AddMinutes(5)).Value.Resume(BreastSide.Left, T0.AddMinutes(9)).Value;

        // Act
        var parts = feed.Parts;

        // Assert
        parts.Should().Be(2);
    }

    // ---- AreCloseTogether ----

    [Fact]
    public void AreCloseTogether_BreastFeedsPauseWithinFifteenMinutes_IsTrue()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(10 + 15), 5);

        // Act
        var close = FeedMerge.AreCloseTogether(b, a);

        // Assert
        close.Should().BeTrue("the pause is measured from the end of the earlier feed, in either argument order");
    }

    [Fact]
    public void AreCloseTogether_BreastPauseOverFifteenMinutes_IsFalse()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(10 + 16), 5);

        // Act
        var close = FeedMerge.AreCloseTogether(a, b);

        // Assert
        close.Should().BeFalse();
    }

    [Fact]
    public void AreCloseTogether_StartsWithinFifteenMinutesButLongFeed_IsTrueAsAnOverlap()
    {
        // Arrange
        var a = Breast(T0, 60);
        var b = Breast(T0.AddMinutes(10), 5);

        // Act
        var close = FeedMerge.AreCloseTogether(a, b);

        // Assert
        close.Should().BeTrue("overlapping feeds are close; Plan refuses them with Overlap");
    }

    [Fact]
    public void AreCloseTogether_BottlesFifteenAndSixteenMinutesApart_IsTrueThenFalse()
    {
        // Arrange
        var a = A.Bottle(T0);

        // Act
        var at15 = FeedMerge.AreCloseTogether(a, A.Bottle(T0.AddMinutes(15)));
        var at16 = FeedMerge.AreCloseTogether(a, A.Bottle(T0.AddMinutes(16)));

        // Assert
        at15.Should().BeTrue();
        at16.Should().BeFalse();
    }

    [Fact]
    public void AreCloseTogether_BreastAndBottle_IsFalse()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = A.Bottle(T0.AddMinutes(11));

        // Act
        var close = FeedMerge.AreCloseTogether(a, b);

        // Assert
        close.Should().BeFalse();
    }

    [Fact]
    public void AreCloseTogether_DifferentBabies_IsFalse()
    {
        // Arrange
        var a = Breast(T0, 10);
        var b = Breast(T0.AddMinutes(11), 5, baby: A.OtherBaby);

        // Act
        var close = FeedMerge.AreCloseTogether(a, b);

        // Assert
        close.Should().BeFalse();
    }
}
