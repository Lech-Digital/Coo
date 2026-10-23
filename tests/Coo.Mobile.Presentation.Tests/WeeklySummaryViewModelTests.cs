using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

[Collection(nameof(StringsState))]
public sealed class WeeklySummaryViewModelTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private readonly IAdvisorApi _api = Substitute.For<IAdvisorApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 14, 0, 0, TimeSpan.FromHours(2)));

    public WeeklySummaryViewModelTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns(
            [HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns(
            [BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 23)).Value]);
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public async Task Write_Answered_ShowsItAndDoesNotAskAgainThisWeek()
    {
        // Arrange
        _api.AskAsync(Arg.Any<AskAdvisorRequest>(), Arg.Any<CancellationToken>()).Returns(new AskAdvisorResponse { Answer = " Lena slept more this week. " });
        var model = new WeeklySummaryViewModel(_api, _households, _time);

        // Act
        await model.WriteCommand.ExecuteAsync(null);
        await model.WriteCommand.ExecuteAsync(null);

        // Assert
        model.Text.Should().Be("Lena slept more this week.");
        model.IsError.Should().BeFalse();
        await _api.Received(1).AskAsync(Arg.Is<AskAdvisorRequest>(r => !r.Question!.Contains("Lena", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_FreeLimitReached_SaysSoAndCanBeTriedAgain()
    {
        // Arrange
        _api.AskAsync(Arg.Any<AskAdvisorRequest>(), Arg.Any<CancellationToken>()).Returns(new AskAdvisorResponse { ErrorCode = "advisor.free-limit", Limit = 3 });
        var model = new WeeklySummaryViewModel(_api, _households, _time);

        // Act
        await model.WriteCommand.ExecuteAsync(null);
        await model.WriteCommand.ExecuteAsync(null);

        // Assert
        model.IsError.Should().BeTrue();
        model.Text.Should().Contain("3");
        await _api.Received(2).AskAsync(Arg.Any<AskAdvisorRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_ConsentDeclined_AsksNothing()
    {
        // Arrange
        var model = new WeeklySummaryViewModel(_api, _households, _time) { EnsureConsentAsync = () => Task.FromResult(false) };

        // Act
        await model.WriteCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().AskAsync(default!, default);
        model.Text.Should().BeEmpty();
    }
}
