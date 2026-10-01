using Coo.Domain.CareLog;

namespace Coo.Domain.Tests.CareLog;

public sealed class ValueObjectTests
{
    [Fact]
    public void FromOunces_FourOunces_RoundTripsToFourPointZero()
    {
        // Arrange
        const decimal ounces = 4m;

        // Act
        var volume = Volume.FromOunces(ounces).Value;

        // Assert
        volume.Millilitres.Should().Be(118.3m);
        Math.Round(volume.Ounces, 1).Should().Be(4.0m);
    }

    [Theory]
    [InlineData(1000.0, true)]
    [InlineData(1000.1, false)]
    [InlineData(0.1, true)]
    [InlineData(0.0, false)]
    [InlineData(-5.0, false)]
    public void FromMillilitres_AtAndPastBounds_AcceptsOnlyInRange(double millilitres, bool expected)
    {
        // Arrange
        var value = (decimal)millilitres;

        // Act
        var result = Volume.FromMillilitres(value);

        // Assert
        result.IsSuccess.Should().Be(expected);
        if (!expected)
            result.Error!.Code.Should().Be(Volume.OutOfRange);
    }

    [Fact]
    public void Create_EndEqualToStart_FailsWithEndNotAfterStart()
    {
        // Arrange
        var at = A.Now;

        // Act
        var result = TimeRange.Create(at, at);

        // Assert
        result.Error!.Code.Should().Be(TimeRange.EndNotAfterStart);
    }

    [Fact]
    public void Overlaps_OpenRangeAndLaterRangeBeforeNow_ReturnsTrue()
    {
        // Arrange
        var open = TimeRange.Open(A.Now.AddHours(-2));
        var later = TimeRange.Create(A.Now.AddHours(-1), A.Now.AddMinutes(-30)).Value;

        // Act
        var overlaps = open.Overlaps(later, A.Now);

        // Assert
        overlaps.Should().BeTrue("an open range is treated as ending at now");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void From_EmptyOrWhitespace_ReturnsNoNote(string? text)
    {
        // Arrange
        var input = text;

        // Act
        var result = Note.From(input);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void From_501Characters_FailsWithTooLong()
    {
        // Arrange
        var text = new string('a', 501);

        // Act
        var result = Note.From(text);

        // Assert
        result.Error!.Code.Should().Be(Note.TooLong);
    }

    [Theory]
    [InlineData("feeding", true)]
    [InlineData("tummy-time", true)]
    [InlineData("Feeding", false)]
    [InlineData("f", false)]
    [InlineData("1sleep", false)]
    [InlineData("", false)]
    public void From_KindCodes_AcceptsOnlyTheCodeFormat(string code, bool expected)
    {
        // Arrange
        var input = code;

        // Act
        var result = EntryKindCode.From(input);

        // Assert
        result.IsSuccess.Should().Be(expected);
    }
}
