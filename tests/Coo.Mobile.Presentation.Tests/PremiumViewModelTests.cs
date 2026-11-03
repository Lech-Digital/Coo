using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
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
    private readonly IFamilyStore _families = Substitute.For<IFamilyStore>();
    private readonly Account _account = new(new MemorySessions());

    private PremiumViewModel NewModel() => new(_billing, _households, _account, _families);

    private async Task SignInAsync(Role role, HouseholdId household)
    {
        var user = new UserId(Guid.Parse("0190a000-0000-7000-8000-0000000000a1"));
        await _account.SignedInAsync(
            new StoredSession("a", DateTimeOffset.MaxValue, "r", new AccountProfile(user, "Ann", "ann@example.com"), household, JoinPending: false),
            CancellationToken.None);
        _families.ListAsync(user, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(household, role, DateTimeOffset.MinValue, "Ann", 2)]);
    }

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
        await SignInAsync(Role.Owner, new HouseholdId(Guid.Parse("0190a000-0000-7000-8000-0000000000b2")));
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _billing.GetOffersAsync(Arg.Any<CancellationToken>()).Returns([new PremiumOffer(PremiumPlan.Monthly, "€4.79", 4_790_000, false)]);
        var model = NewModel();

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
        var model = NewModel();

        // Act
        await model.InitializeAsync();

        // Assert
        model.Selected.Should().BeNull();
        model.Terms.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_SignedOut_AsksToSignInBeforeBuying()
    {
        // Arrange: a purchase made signed out could never reach the family on the server.
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _billing.GetOffersAsync(Arg.Any<CancellationToken>()).Returns([new PremiumOffer(PremiumPlan.Yearly, "€29.99", 29_990_000, true)]);
        var model = NewModel();

        // Act
        await model.InitializeAsync();

        // Assert
        model.IsSignedIn.Should().BeFalse();
        model.CanBuy.Should().BeTrue();
        model.BuyLabel.Should().Be("Sign in to buy");
    }

    [Theory]
    [InlineData(Role.Caregiver)]
    [InlineData(Role.Viewer)]
    public async Task InitializeAsync_SignedInAsCaregiverOrViewer_CannotBuy(Role role)
    {
        // Arrange
        var household = SettingsFor(out var id);
        await SignInAsync(role, id);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([household]);
        _billing.GetOffersAsync(Arg.Any<CancellationToken>()).Returns([]);
        var model = NewModel();

        // Act
        await model.InitializeAsync();

        // Assert
        model.IsSignedIn.Should().BeTrue();
        model.CanBuy.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_SignedInAsParent_CanBuyAndSeesThePlanLabel()
    {
        // Arrange
        var household = SettingsFor(out var id);
        await SignInAsync(Role.Parent, id);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([household]);
        _billing.GetOffersAsync(Arg.Any<CancellationToken>()).Returns([new PremiumOffer(PremiumPlan.Yearly, "€29.99", 29_990_000, true)]);
        var model = NewModel();

        // Act
        await model.InitializeAsync();

        // Assert
        model.CanBuy.Should().BeTrue();
        model.BuyLabel.Should().Be("Start 7 days free");
    }

    [Fact]
    public async Task RestoreCommand_NothingBoughtOnThisAccount_SaysSo()
    {
        // Arrange
        var household = SettingsFor(out var id);
        await SignInAsync(Role.Owner, id);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([household]);
        _billing.CheckEntitlementAsync(id, Arg.Any<CancellationToken>()).Returns(false);
        _billing.GetOffersAsync(Arg.Any<CancellationToken>()).Returns([]);
        var model = NewModel();

        // Act
        await model.RestoreCommand.ExecuteAsync(null);

        // Assert
        model.IsPremium.Should().BeFalse();
        model.Notice.Should().NotBeEmpty();
        await _billing.Received().CheckEntitlementAsync(id, Arg.Any<CancellationToken>());
    }

    private static HouseholdSettings SettingsFor(out HouseholdId id)
    {
        id = new HouseholdId(Guid.Parse("0190a000-0000-7000-8000-0000000000b1"));
        return HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, new DeviceId(Guid.Parse("0190a000-0000-7000-8000-0000000000c1")))).Value;
    }
}
