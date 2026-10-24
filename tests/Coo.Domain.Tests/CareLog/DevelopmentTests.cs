using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Development;
using Coo.Domain.Common;

namespace Coo.Domain.Tests.CareLog;

public sealed class DevelopmentTests
{
    [Fact]
    public void SolidsCreate_FoodAndAmountWithSpaces_AreTrimmedAndEmptyAmountIsNull()
    {
        // Act
        var result = SolidsDetails.Create("  Carrot puree ", " ", SolidsReaction.Liked);

        // Assert
        result.Value.Food.Should().Be("Carrot puree");
        result.Value.Amount.Should().BeNull();
        result.Value.Reaction.Should().Be(SolidsReaction.Liked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SolidsCreate_NoFood_FailsWithFoodRequired(string? food)
    {
        // Act
        var result = SolidsDetails.Create(food, null, SolidsReaction.None);

        // Assert
        result.Error!.Code.Should().Be(DevelopmentErrors.FoodRequired);
    }

    [Fact]
    public void SolidsCreate_FoodTooLongOrUnknownReaction_Fails()
    {
        // Act
        var tooLong = SolidsDetails.Create(new string('a', SolidsDetails.MaxFoodLength + 1), null, SolidsReaction.None);
        var unknown = SolidsDetails.Create("Banana", null, (SolidsReaction)42);

        // Assert
        tooLong.Error!.Code.Should().Be(DevelopmentErrors.TextTooLong);
        unknown.Error!.Code.Should().Be(DevelopmentErrors.ReactionUnknown);
    }

    [Fact]
    public void MilestoneCreate_PresetInAnyCase_IsTheLowercasePreset()
    {
        // Act
        var result = MilestoneDetails.Create(" First-Smile ", null);

        // Assert
        result.Value.Code.Should().Be("first-smile");
        result.Value.Title.Should().BeNull();
    }

    [Fact]
    public void MilestoneCreate_CustomWithoutTitle_FailsWithTitleRequired()
    {
        // Act
        var result = MilestoneDetails.Create(MilestoneDetails.Custom, "  ");

        // Assert
        result.Error!.Code.Should().Be(DevelopmentErrors.MilestoneTitleRequired);
    }

    [Fact]
    public void MilestoneCreate_UnknownCode_FailsWithUnknown()
    {
        // Act
        var result = MilestoneDetails.Create("juggles", null);

        // Assert
        result.Error!.Code.Should().Be(DevelopmentErrors.MilestoneUnknown);
    }

    [Fact]
    public void TummyKind_SessionOverTwoHours_IsRefused()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(2));
        var tooLong = EntryTiming.Session(TimeRange.Create(start, start.AddHours(2.5)).Value);
        var fine = EntryTiming.Session(TimeRange.Create(start, start.AddMinutes(12)).Value);

        // Act
        var refused = ((IEntryKind)TummyKind.Instance).Validate(new TummyDetails(), tooLong);
        var accepted = ((IEntryKind)TummyKind.Instance).Validate(new TummyDetails(), fine);

        // Assert
        refused.Error!.Code.Should().Be(DevelopmentErrors.TummyTooLong);
        accepted.IsSuccess.Should().BeTrue();
    }
}
