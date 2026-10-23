using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;

namespace Coo.Domain.Tests.CareLog;

public sealed class GrowthAndHealthTests
{
    [Fact]
    public void GrowthCreate_WeightOnly_Succeeds()
    {
        // Act
        var result = GrowthDetails.Create(4_380, null, null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.WeightGrams.Should().Be(4_380);
        result.Value.LengthMm.Should().BeNull();
    }

    [Fact]
    public void GrowthCreate_NothingMeasured_FailsWithEmpty()
    {
        // Act
        var result = GrowthDetails.Create(null, null, null);

        // Assert
        result.Error!.Code.Should().Be(GrowthErrors.Empty);
    }

    [Theory]
    [InlineData(299, null, null, GrowthErrors.WeightOutOfRange)]
    [InlineData(null, 1_301, null, GrowthErrors.LengthOutOfRange)]
    [InlineData(null, null, 199, GrowthErrors.HeadOutOfRange)]
    public void GrowthCreate_ValueOutsideItsRange_FailsWithThatError(int? weight, int? length, int? head, string error)
    {
        // Act
        var result = GrowthDetails.Create(weight, length, head);

        // Assert
        result.Error!.Code.Should().Be(error);
    }

    [Fact]
    public void MedicineCreate_NameWithSpaces_IsTrimmedAndEmptyDoseIsNull()
    {
        // Act
        var result = MedicineDetails.Create("  Vitamin D ", "  ");

        // Assert
        result.Value.Name.Should().Be("Vitamin D");
        result.Value.Dose.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MedicineCreate_NoName_FailsWithNameRequired(string? name)
    {
        // Act
        var result = MedicineDetails.Create(name, null);

        // Assert
        result.Error!.Code.Should().Be(HealthErrors.NameRequired);
    }

    [Fact]
    public void VaccineCreate_NameLongerThanSixty_FailsWithTooLong()
    {
        // Act
        var result = VaccineDetails.Create(new string('x', 61));

        // Assert
        result.Error!.Code.Should().Be(HealthErrors.TextTooLong);
    }

    [Theory]
    [InlineData(38.04, 38.0, true)]
    [InlineData(37.95, 38.0, true)]
    [InlineData(36.6, 36.6, false)]
    public void TemperatureCreate_RoundsToOneDecimalAndMarksFever(double input, double expected, bool fever)
    {
        // Act
        var result = TemperatureDetails.Create((decimal)input);

        // Assert
        result.Value.Celsius.Should().Be((decimal)expected);
        result.Value.IsFever.Should().Be(fever);
    }

    [Theory]
    [InlineData(33.9)]
    [InlineData(100.4)]
    public void TemperatureCreate_OutsideHumanRange_Fails(double input)
    {
        // Act
        var result = TemperatureDetails.Create((decimal)input);

        // Assert
        result.Error!.Code.Should().Be(HealthErrors.TemperatureOutOfRange);
    }
}
