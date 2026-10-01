using Coo.Domain.Households;

namespace Coo.Domain.Tests.Households;

public sealed class BabyTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    [Theory]
    [InlineData("Łucja", "lucja")]
    [InlineData("Straße", "strasse")]
    [InlineData("  Zosia   Maria ", "zosia maria")]
    [InlineData("Zoë", "zoe")]
    [InlineData("Søren", "soren")]
    public void From_LettersAndSpacing_MapToComparisonKey(string name, string expected)
    {
        // Arrange
        var input = name;

        // Act
        var key = NameKey.From(input);

        // Assert
        key.Should().Be(expected);
    }

    [Fact]
    public void Create_NicknameEqualsNameByKey_FailsWithNicknameDuplicate()
    {
        // Arrange
        string[] nicknames = ["Mimi", "LUCJA"];

        // Act
        var result = BabyProfile.Create(A.Baby, A.Household, "Łucja", nicknames, Today.AddMonths(-3), null, A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.NicknameDuplicate);
    }

    [Fact]
    public void Create_BirthDateTwoDaysAhead_FailsWithBirthInFuture()
    {
        // Arrange
        var birth = Today.AddDays(2);

        // Act
        var result = BabyProfile.Create(A.Baby, A.Household, "Maja", [], birth, null, A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.BirthInFuture);
    }

    [Fact]
    public void Create_DueDate25WeeksAfterBirth_FailsWithDueDateOutOfRange()
    {
        // Arrange
        var birth = Today.AddMonths(-2);

        // Act
        var result = BabyProfile.Create(A.Baby, A.Household, "Maja", [], birth, birth.AddDays(25 * 7), A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.DueDateOutOfRange);
    }

    [Fact]
    public void On_PretermUnder24Months_ReturnsCorrectedAge()
    {
        // Arrange: born 8 weeks early, 20 weeks ago.
        var birth = Today.AddDays(-140);
        var baby = BabyProfile.Create(A.Baby, A.Household, "Maja", [], birth, birth.AddDays(56), A.Stamp(), Today).Value;

        // Act
        var age = BabyAge.On(baby, Today);

        // Assert
        age.ActualDays.Should().Be(140);
        age.CorrectedDays.Should().Be(84);
        age.IsCorrected.Should().BeTrue();
    }

    [Fact]
    public void On_PretermOver24Months_ReturnsActualAge()
    {
        // Arrange
        var birth = Today.AddMonths(-25);
        var baby = BabyProfile.Create(A.Baby, A.Household, "Maja", [], birth, birth.AddDays(56), A.Stamp(), Today).Value;

        // Act
        var age = BabyAge.On(baby, Today);

        // Assert
        age.IsCorrected.Should().BeFalse();
        age.CorrectedDays.Should().Be(age.ActualDays);
    }

    [Fact]
    public void On_BeforeDueDate_CorrectedIsZeroAndFlagged()
    {
        // Arrange: born 8 weeks early, 3 weeks ago.
        var birth = Today.AddDays(-21);
        var baby = BabyProfile.Create(A.Baby, A.Household, "Maja", [], birth, birth.AddDays(56), A.Stamp(), Today).Value;

        // Act
        var age = BabyAge.On(baby, Today);

        // Assert
        age.CorrectedDays.Should().Be(0);
        age.IsBeforeDueDate.Should().BeTrue();
    }
}

public sealed class ExpectedBabyTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    [Fact]
    public void Create_DueDateOnly_IsExpected()
    {
        // Arrange
        var due = Today.AddDays(42);

        // Act
        var baby = BabyProfile.Create(A.Baby, A.Household, "Baby", [], null, due, A.Stamp(), Today).Value;

        // Assert
        baby.IsBorn.Should().BeFalse();
        baby.DueDate.Should().Be(due);
    }

    [Fact]
    public void Create_NeitherBirthNorDueDate_FailsWithBirthOrDueDateRequired()
    {
        // Arrange
        DateOnly? none = null;

        // Act
        var result = BabyProfile.Create(A.Baby, A.Household, "Baby", [], none, none, A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.BirthOrDueDateRequired);
    }

    [Theory]
    [InlineData(-42, true)]
    [InlineData(-43, false)]
    [InlineData(308, true)]
    [InlineData(309, false)]
    public void Create_ExpectedDueDateAtBounds_AcceptsSixWeeksPastToFortyFourAhead(int days, bool accepted)
    {
        // Arrange
        var due = Today.AddDays(days);

        // Act
        var result = BabyProfile.Create(A.Baby, A.Household, "Baby", [], null, due, A.Stamp(), Today);

        // Assert
        result.IsSuccess.Should().Be(accepted);
    }

    [Fact]
    public void RecordBirth_ExpectedBaby_BecomesBornAndKeepsDueDate()
    {
        // Arrange: born three weeks early.
        var due = Today.AddDays(21);
        var baby = BabyProfile.Create(A.Baby, A.Household, "Baby", [], null, due, A.Stamp(), Today).Value;

        // Act
        var result = baby.RecordBirth(Today, A.Stamp(), Today);

        // Assert
        result.IsSuccess.Should().BeTrue();
        baby.IsBorn.Should().BeTrue();
        BabyAge.On(baby, Today.AddDays(30)).IsCorrected.Should().BeTrue();
    }

    [Fact]
    public void RecordBirth_AlreadyBorn_FailsWithAlreadyBorn()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Maja", [], Today.AddDays(-10), null, A.Stamp(), Today).Value;

        // Act
        var result = baby.RecordBirth(Today, A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.AlreadyBorn);
    }

    [Fact]
    public void On_ExpectedBaby_Throws()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Baby", [], null, Today.AddDays(30), A.Stamp(), Today).Value;

        // Act
        var act = () => BabyAge.On(baby, Today);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revise_AllFields_ChangesThemUnderOneVersion()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", [], Today.AddMonths(-2), null, A.Stamp(), Today).Value;
        var version = A.Stamp();

        // Act
        var result = baby.Revise("Zofia", ["Zo", "Mimi"], Today.AddMonths(-2).AddDays(-1), Today.AddMonths(-1), version, Today);

        // Assert
        result.IsSuccess.Should().BeTrue();
        baby.Name.Should().Be("Zofia");
        baby.Nicknames.Should().Equal("Zo", "Mimi");
        baby.BirthDate.Should().Be(Today.AddMonths(-2).AddDays(-1));
        baby.DueDate.Should().Be(Today.AddMonths(-1));
        baby.Version.Should().Be(version);
    }

    [Fact]
    public void Revise_NameOfSpaces_FailsAndChangesNothing()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", ["Zo"], Today.AddMonths(-2), null, A.Stamp(), Today).Value;
        var before = baby.Version;

        // Act
        var result = baby.Revise("   ", [], Today.AddMonths(-2), null, A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.NameInvalid);
        baby.Name.Should().Be("Zosia");
        baby.Nicknames.Should().Equal("Zo");
        baby.Version.Should().Be(before);
    }

    [Fact]
    public void Revise_BirthDateForExpectedBaby_FailsWithNotBornYet()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Baby", [], null, Today.AddDays(30), A.Stamp(), Today).Value;

        // Act
        var result = baby.Revise("Baby", [], Today, Today.AddDays(30), A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.NotBornYet);
        baby.IsBorn.Should().BeFalse();
    }

    [Fact]
    public void Revise_NameWithSurroundingSpaces_StoresItTrimmed()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", [], Today.AddMonths(-2), null, A.Stamp(), Today).Value;

        // Act
        var result = baby.Revise("  Zofia ", [" Zo "], Today.AddMonths(-2), null, A.Stamp(), Today);

        // Assert
        result.IsSuccess.Should().BeTrue();
        baby.Name.Should().Be("Zofia");
        baby.Nicknames.Should().Equal("Zo");
    }

    [Fact]
    public void Revise_StaleVersion_FailsWithStaleVersion()
    {
        // Arrange
        var stamp = A.Stamp();
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", [], Today.AddMonths(-2), null, stamp, Today).Value;

        // Act
        var result = baby.Revise("Zofia", [], Today.AddMonths(-2), null, stamp, Today);

        // Assert
        result.Error!.Code.Should().Be(Coo.Domain.Common.RecordErrors.StaleVersion);
    }

    [Fact]
    public void Revise_SixNicknames_FailsWithNicknamesInvalid()
    {
        // Arrange: reviewer's finding — six short nicknames were told "Enter a name of up to 40 characters."
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", [], Today.AddMonths(-2), null, A.Stamp(), Today).Value;
        string[] six = ["Zo", "Mimi", "Zuzu", "Bean", "Pip", "Bug"];

        // Act
        var result = baby.Revise("Zosia", six, Today.AddMonths(-2), null, A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.NicknamesInvalid);
    }
}
