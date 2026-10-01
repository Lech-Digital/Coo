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
