using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.MilkStash;

namespace Coo.Domain.Tests.CareLog;

/// <summary>The milk stash: what a bag may be, and how long it keeps (CDC storage guidance).</summary>
public sealed class MilkBagTests
{
    private static readonly DateTimeOffset Expressed = new(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(1));

    private static MilkBagDetails Bag(MilkPlace place, decimal ml = 120) => MilkBagDetails.Create(Volume.FromMillilitres(ml).Value, place).Value;

    [Fact]
    public void UseBy_FridgeFreezerAndThawed_FourDaysSixMonthsAndADay()
    {
        // Arrange
        var fridge = Bag(MilkPlace.Fridge);
        var freezer = Bag(MilkPlace.Freezer);
        var thawed = freezer.Thaw(Expressed.AddDays(30)).Value;

        // Act
        var fridgeBy = MilkStorage.UseBy(fridge, Expressed);
        var freezerBy = MilkStorage.UseBy(freezer, Expressed);
        var thawedBy = MilkStorage.UseBy(thawed, Expressed);

        // Assert
        fridgeBy.Should().Be(Expressed.AddDays(4));
        freezerBy.Should().Be(Expressed.AddMonths(6));
        thawedBy.Should().Be(Expressed.AddDays(31));
        thawed.Place.Should().Be(MilkPlace.Fridge);
    }

    [Fact]
    public void UseBy_ThawedNearSixMonths_DoesNotOutliveTheFreezersLimit()
    {
        // Arrange: thawed an hour before its six months were up.
        var thawed = Bag(MilkPlace.Freezer).Thaw(Expressed.AddMonths(6).AddHours(-1)).Value;

        // Act
        var useBy = MilkStorage.UseBy(thawed, Expressed);

        // Assert
        useBy.Should().Be(Expressed.AddMonths(6));
    }

    [Theory]
    [InlineData(MilkPlace.Fridge, 2, MilkFreshness.Fine)]
    [InlineData(MilkPlace.Fridge, 3.5, MilkFreshness.UseSoon)]
    [InlineData(MilkPlace.Fridge, 4, MilkFreshness.Expired)]
    [InlineData(MilkPlace.Freezer, 100, MilkFreshness.Fine)]
    [InlineData(MilkPlace.Freezer, 175, MilkFreshness.UseSoon)]
    public void Freshness_DaysAfterExpressing_WarnsBeforeTheUseBy(MilkPlace place, double days, MilkFreshness expected)
    {
        // Act
        var freshness = MilkStorage.Freshness(Bag(place), Expressed, Expressed.AddDays(days));

        // Assert
        freshness.Should().Be(expected);
    }

    [Fact]
    public void Freeze_ThawedMilk_IsRefused()
    {
        // Arrange
        var thawed = Bag(MilkPlace.Freezer).Thaw(Expressed.AddDays(2)).Value;

        // Act
        var refrozen = thawed.Freeze();
        var thawAgain = thawed.Thaw(Expressed.AddDays(3));

        // Assert
        refrozen.Error!.Code.Should().Be(MilkBagErrors.Refreeze);
        thawAgain.Error!.Code.Should().Be(MilkBagErrors.NotFrozen);
    }

    [Fact]
    public void Create_ALongLabelOrABlankOne_IsRefusedOrDropped()
    {
        // Act
        var label = MilkBagDetails.Create(Volume.FromMillilitres(100).Value, MilkPlace.Fridge, label: new string('x', 41));
        var blank = MilkBagDetails.Create(Volume.FromMillilitres(100).Value, MilkPlace.Fridge, label: "   ");

        // Assert
        label.Error!.Code.Should().Be(MilkBagErrors.LabelTooLong);
        blank.Value.Label.Should().BeNull();
    }

    [Fact]
    public void UseThenRestock_ABagUsedByMistake_IsBackInStock()
    {
        // Arrange
        var bag = Bag(MilkPlace.Fridge);

        // Act
        var used = bag.Use(new DateOnly(2026, 11, 2)).Value;
        var back = used.Restock().Value;
        var discarded = bag.Discard(new DateOnly(2026, 11, 2)).Value;

        // Assert
        used.IsInStock.Should().BeFalse();
        back.IsInStock.Should().BeTrue();
        back.Should().Be(bag);
        discarded.IsInStock.Should().BeFalse();
    }
}
