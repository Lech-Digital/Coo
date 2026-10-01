using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;

namespace Coo.Domain.Tests.CareLog;

public sealed class DuplicateDetectorTests
{
    private static readonly HashSet<EntryPair> NoneDismissed = [];
    private static readonly DateTimeOffset T0 = A.Now.AddHours(-3);

    public static TheoryData<string, int, bool> Tolerances => new()
    {
        { "feeding", 15, true },
        { "feeding", 16, false },
        { "diaper", 10, true },
        { "diaper", 11, false },
        { "pump", 15, true },
        { "pump", 16, false },
    };

    [Theory]
    [MemberData(nameof(Tolerances))]
    public void Find_SameKindAtAndPastTolerance_FlagsOnlyWithinTolerance(string kind, int minutesApart, bool flagged)
    {
        // Arrange
        var (a, b) = kind switch
        {
            "feeding" => (A.Bottle(T0), A.Bottle(T0.AddMinutes(minutesApart))),
            "diaper" => (A.Diaper(T0), A.Diaper(T0.AddMinutes(minutesApart))),
            _ => (A.Pump(T0, T0.AddMinutes(5)), A.Pump(T0.AddMinutes(minutesApart), T0.AddMinutes(minutesApart + 5))),
        };

        // Act
        var pairs = DuplicateDetector.Find([a], [b], NoneDismissed, A.Now);

        // Assert
        pairs.Should().HaveCount(flagged ? 1 : 0);
    }

    [Fact]
    public void Find_DiapersWithDifferentContents_StillFlags()
    {
        // Arrange
        var wet = A.Diaper(T0, contents: DiaperContents.Wet);
        var mixed = A.Diaper(T0.AddMinutes(3), contents: DiaperContents.Mixed);

        // Act
        var pairs = DuplicateDetector.Find([wet], [mixed], NoneDismissed, A.Now);

        // Assert
        pairs.Should().ContainSingle();
    }

    [Fact]
    public void Find_BreastAndBottleWithinFiveMinutes_ReturnsNoPair()
    {
        // Arrange
        var breast = A.Breast(T0);
        var topUp = A.Bottle(T0.AddMinutes(5), milk: Milk.ExpressedBreastMilk);

        // Act
        var pairs = DuplicateDetector.Find([topUp], [breast], NoneDismissed, A.Now);

        // Assert
        pairs.Should().BeEmpty("a top-up bottle after the breast is normal");
    }

    [Fact]
    public void Find_SameKindDifferentBabies_ReturnsNoPair()
    {
        // Arrange: twins changed together.
        var first = A.Diaper(T0, A.Baby);
        var second = A.Diaper(T0.AddMinutes(2), A.OtherBaby);

        // Act
        var pairs = DuplicateDetector.Find([first], [second], NoneDismissed, A.Now);

        // Assert
        pairs.Should().BeEmpty();
    }

    [Fact]
    public void Find_PumpsByDifferentPeople_ReturnsNoPair()
    {
        // Arrange
        var mine = A.Pump(T0, T0.AddMinutes(20), A.Parent);
        var theirs = A.Pump(T0.AddMinutes(1), T0.AddMinutes(21), A.OtherParent);

        // Act
        var pairs = DuplicateDetector.Find([mine], [theirs], NoneDismissed, A.Now);

        // Assert
        pairs.Should().BeEmpty();
    }

    [Fact]
    public void Find_PairDismissedInReverseOrder_ReturnsNoPair()
    {
        // Arrange
        var a = A.Diaper(T0);
        var b = A.Diaper(T0.AddMinutes(1));
        var dismissed = new HashSet<EntryPair> { new(b.Id, a.Id) };

        // Act
        var pairs = DuplicateDetector.Find([a], [b], dismissed, A.Now);

        // Assert
        pairs.Should().BeEmpty();
    }

    [Fact]
    public void Find_DeletedExistingEntry_ReturnsNoPair()
    {
        // Arrange
        var a = A.Diaper(T0);
        var b = A.Diaper(T0.AddMinutes(1));
        b.Delete(A.Stamp());

        // Act
        var pairs = DuplicateDetector.Find([a], [b], NoneDismissed, A.Now);

        // Assert
        pairs.Should().BeEmpty();
    }

    [Fact]
    public void Find_TwoOpenSleepsSameBaby_ReturnsPair()
    {
        // Arrange: two caregivers each started a sleep while offline.
        var mine = A.Sleep(A.Now.AddMinutes(-40), null);
        var theirs = A.Sleep(A.Now.AddMinutes(-35), null);

        // Act
        var pairs = DuplicateDetector.Find([theirs], [mine], NoneDismissed, A.Now);

        // Assert
        pairs.Should().ContainSingle();
    }

    [Fact]
    public void Find_BackToBackSleeps_ReturnsNoPair()
    {
        // Arrange
        var nap = A.Sleep(T0, T0.AddMinutes(45));
        var next = A.Sleep(T0.AddMinutes(45), T0.AddMinutes(90));

        // Act
        var pairs = DuplicateDetector.Find([next], [nap], NoneDismissed, A.Now);

        // Assert
        pairs.Should().BeEmpty("touching is not overlapping");
    }

    [Fact]
    public void Find_SamePairReachedFromBothSides_ReturnsItOnce()
    {
        // Arrange
        var a = A.Diaper(T0);
        var b = A.Diaper(T0.AddMinutes(1));

        // Act
        var pairs = DuplicateDetector.Find([a, b], [a, b], NoneDismissed, A.Now);

        // Assert
        pairs.Should().ContainSingle();
    }
}
