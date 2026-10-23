using Coo.Domain.Common;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using FluentAssertions;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

[Collection(nameof(StringsState))]
public sealed class PremiumViewModelTests : IDisposable
{
    private readonly IBillingService _billing = Substitute.For<IBillingService>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();

    public PremiumViewModelTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Rows_YearlyCheaperThanTwelveMonths_ShowsTheSavingAndKeepsPlanOrder()
    {
        // Arrange: €4.79 a month against €29.99 a year saves 47%.
        PremiumOffer[] offers =
        [
            new(PremiumPlan.Lifetime, "€59.99", 59_990_000, false),
            new(PremiumPlan.Yearly, "€29.99", 29_990_000, true),
            new(PremiumPlan.Monthly, "€4.79", 4_790_000, true),
        ];

        // Act
        var rows = PremiumViewModel.Rows(offers);

        // Assert
        rows.Select(r => r.Plan).Should().Equal(PremiumPlan.Monthly, PremiumPlan.Yearly, PremiumPlan.Lifetime);
        rows[1].Badge.Should().Be("Save 47%");
        rows[1].Price.Should().Be("€29.99 / year");
        rows[0].Badge.Should().BeNull();
        rows[2].Price.Should().Be("€59.99, once");
    }

    [Fact]
    public void Rows_WithoutAMonthlyPrice_ShowsNoSaving()
    {
        // Arrange
        PremiumOffer[] offers = [new(PremiumPlan.Yearly, "€29.99", 29_990_000, false)];

        // Act
        var rows = PremiumViewModel.Rows(offers);

        // Assert
        rows.Should().ContainSingle().Which.Badge.Should().BeNull();
    }

    [Fact]
    public async Task InitializeAsync_PlayHasNoYearlyPlan_ChoosesTheFirstOfferedAndItsTerms()
    {
        // Arrange
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _billing.GetOffersAsync(Arg.Any<CancellationToken>()).Returns([new PremiumOffer(PremiumPlan.Monthly, "€4.79", 4_790_000, false)]);
        var model = new PremiumViewModel(_billing, _households);

        // Act
        await model.InitializeAsync();

        // Assert
        model.SelectedPlan.Should().Be(PremiumPlan.Monthly);
        model.BuyLabel.Should().Be("Subscribe");
        model.Terms.Should().Contain("€4.79 / month").And.Contain("renews automatically");
    }

    [Fact]
    public async Task InitializeAsync_PlayUnreachable_OffersNothingToBuy()
    {
        // Arrange
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _billing.GetOffersAsync(Arg.Any<CancellationToken>()).Returns([]);
        var model = new PremiumViewModel(_billing, _households);

        // Act
        await model.InitializeAsync();

        // Assert
        model.Selected.Should().BeNull();
        model.Terms.Should().BeEmpty();
    }
}
