using Coo.Mobile.Application.Growth;
using FluentAssertions;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class WhoGrowthTests
{
    [Fact]
    public void Percentile_BoysMedianBirthWeight_IsTheFiftieth()
    {
        // Act: WHO's median weight for boys at birth is 3.3464 kg.
        var percentile = WhoGrowth.Percentile(GrowthMeasure.Weight, ChartSex.Boy, 0, 3.3464);

        // Assert
        percentile.Should().BeApproximately(50, 0.01);
    }

    [Theory]
    [InlineData(GrowthMeasure.Weight, ChartSex.Girl, 6, -1.644854, 5.97888)]
    [InlineData(GrowthMeasure.Length, ChartSex.Boy, 12, 1.644854, 79.65737)]
    [InlineData(GrowthMeasure.Head, ChartSex.Girl, 2, 0, 38.2521)]
    public void ValueAt_PublishedPercentiles_MatchTheWhoTables(GrowthMeasure measure, ChartSex sex, double months, double z, double expected)
    {
        // Act: the 5th, 95th and 50th percentiles as WHO publishes them.
        var value = WhoGrowth.ValueAt(measure, sex, months, z);

        // Assert
        value.Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void Percentile_ValueOnTheNinetySeventhLine_RoundTripsToNinetySeven()
    {
        // Arrange: halfway between months, so interpolated.
        var value = WhoGrowth.ValueAt(GrowthMeasure.Weight, ChartSex.Girl, 4.5, 1.880794)!.Value;

        // Act
        var percentile = WhoGrowth.Percentile(GrowthMeasure.Weight, ChartSex.Girl, 4.5, value);

        // Assert
        percentile.Should().BeApproximately(97, 0.01);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(24.1)]
    public void Percentile_OutsideZeroToTwentyFourMonths_IsNull(double months)
    {
        // Act
        var percentile = WhoGrowth.Percentile(GrowthMeasure.Weight, ChartSex.Boy, months, 10);

        // Assert
        percentile.Should().BeNull();
    }
}
