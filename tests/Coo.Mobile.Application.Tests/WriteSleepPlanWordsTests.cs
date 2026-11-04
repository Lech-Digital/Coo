using Coo.Domain.Common;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Insights;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 019 §4: the plan's words are Premium, written once per plan state, and never in the plan's way.</summary>
public sealed class WriteSleepPlanWordsTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private const string Facts = "Baby: {baby}, 26 weeks old. Bedtime 19:00.";
    private const string Key = "plan-key";

    private readonly IAdvisorApi _api = Substitute.For<IAdvisorApi>();
    private readonly ISleepPlanWordsStore _store = Substitute.For<ISleepPlanWordsStore>();
    private readonly IBillingService _billing = Substitute.For<IBillingService>();

    private WriteSleepPlanWords Words => new(_api, _store, _billing);

    private Task<string?> Write() =>
        Words.ExecuteAsync(Household, Baby, SleepPlanWordsKind.CheckIn, Key, Facts, "pl", "Europe/Amsterdam", CancellationToken.None);

    [Fact]
    public async Task ExecuteAsync_PremiumAndNothingKept_AsksTheServerAndKeepsTheWords()
    {
        // Arrange
        _billing.IsPremiumCached.Returns(true);
        SleepPlanWordsRequest? sent = null;
        _api.WriteSleepPlanAsync(Arg.Do<SleepPlanWordsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new SleepPlanWordsResponse { Text = "{baby} slept longer this week." });

        // Act
        var text = await Write();

        // Assert
        text.Should().Be("{baby} slept longer this week.");
        (sent!.Kind, sent.Facts, sent.Language, sent.TimeZoneId).Should().Be(("checkin", Facts, "pl", "Europe/Amsterdam"));
        (sent.HouseholdId, sent.BabyId).Should().Be((Household.Value, Baby.Value));
        await _store.Received(1).SaveAsync(Key, "{baby} slept longer this week.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WordsAlreadyKept_ReturnsThemWithoutAskingAgain()
    {
        // Arrange
        _billing.IsPremiumCached.Returns(true);
        _store.GetAsync(Key, Arg.Any<CancellationToken>()).Returns("Kept words.");

        // Act
        var text = await Write();

        // Assert
        text.Should().Be("Kept words.");
        await _api.DidNotReceiveWithAnyArgs().WriteSleepPlanAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutPremium_HasNoWordsAndDoesNotCallTheServer()
    {
        // Arrange
        _billing.IsPremiumCached.Returns(false);

        // Act
        var text = await Write();

        // Assert
        text.Should().BeNull();
        await _api.DidNotReceiveWithAnyArgs().WriteSleepPlanAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ServerRefusesOrIsOffline_HasNoWordsAndKeepsNothing()
    {
        // Arrange
        _billing.IsPremiumCached.Returns(true);
        _api.WriteSleepPlanAsync(Arg.Any<SleepPlanWordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SleepPlanWordsResponse { ErrorCode = "advisor.daily-limit", Limit = 10 });
        _api.WriteSleepPlanAsync(Arg.Is<SleepPlanWordsRequest>(r => r.Language == "nl"), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("offline"));

        // Act
        var refused = await Write();
        var offline = await Words.ExecuteAsync(Household, Baby, SleepPlanWordsKind.Plan, Key, Facts, "nl", "UTC", CancellationToken.None);

        // Assert
        refused.Should().BeNull();
        offline.Should().BeNull();
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }
}
