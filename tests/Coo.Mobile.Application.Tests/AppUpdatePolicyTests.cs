using Coo.Mobile.Application.AppVersion;
using FluentAssertions;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class AppUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Decide_NormalRelease_DownloadsInTheBackgroundOncePerSession()
    {
        // Act
        var first = UpdatePolicy.Decide(17, new AppVersionPolicy(18, 15), true, 0, true, true, flexibleOfferedThisSession: false);
        var again = UpdatePolicy.Decide(17, new AppVersionPolicy(18, 15), true, 0, true, true, flexibleOfferedThisSession: true);

        // Assert
        first.Should().Be(UpdateAction.Flexible);
        again.Should().Be(UpdateAction.None);
    }

    [Theory]
    [InlineData(17, 18, 0)]
    [InlineData(17, 15, 4)]
    public void Decide_BelowTheServersMinimumOrHighPriority_IsImmediate(int versionCode, int minimum, int priority)
    {
        // Act
        var action = UpdatePolicy.Decide(versionCode, new AppVersionPolicy(19, minimum), true, priority, true, true, false);

        // Assert
        action.Should().Be(UpdateAction.Immediate);
    }

    [Fact]
    public void Decide_RequiredButPlayHasNothingToOffer_SendsToTheStore()
    {
        // Act
        var action = UpdatePolicy.Decide(16, new AppVersionPolicy(18, 17), false, 0, false, false, false);

        // Assert
        action.Should().Be(UpdateAction.OpenStore);
    }

    [Fact]
    public void Decide_ServerUnreachableAndNoUpdate_DoesNothing()
    {
        // Act
        var action = UpdatePolicy.Decide(17, null, false, 0, false, false, false);

        // Assert
        action.Should().Be(UpdateAction.None);
    }

    [Fact]
    public void ShouldSuggest_ServerKnowsANewerVersionAndNotAskedToday_Suggests()
    {
        // Act: 1.0.0 (17) and the server's latest is 1.8.0 (25); Play said nothing.
        var never = UpdatePolicy.ShouldSuggest(17, new AppVersionPolicy(25, 15), null, Now);
        var yesterday = UpdatePolicy.ShouldSuggest(17, new AppVersionPolicy(25, 15), Now.AddDays(-1), Now);

        // Assert
        never.Should().BeTrue();
        yesterday.Should().BeTrue();
    }

    [Theory]
    [InlineData(25, 25)]
    [InlineData(26, 25)]
    public void ShouldSuggest_AlreadyOnTheLatest_DoesNotSuggest(int versionCode, int latest)
    {
        // Act
        var suggest = UpdatePolicy.ShouldSuggest(versionCode, new AppVersionPolicy(latest, 15), null, Now);

        // Assert
        suggest.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuggest_SuggestedWithinTheDayOrServerUnreachable_DoesNotSuggest()
    {
        // Act
        var recently = UpdatePolicy.ShouldSuggest(17, new AppVersionPolicy(25, 15), Now.AddHours(-3), Now);
        var offline = UpdatePolicy.ShouldSuggest(17, null, null, Now);

        // Assert
        recently.Should().BeFalse();
        offline.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuggest_ClockMovedBackBeforeTheLastSuggestion_SuggestsAgain()
    {
        // Act
        var suggest = UpdatePolicy.ShouldSuggest(17, new AppVersionPolicy(25, 15), Now.AddDays(3), Now);

        // Assert
        suggest.Should().BeTrue();
    }

    [Fact]
    public void ShouldAsk_AfterAWeekWithEnoughEntriesInTheDay_Asks()
    {
        // Act
        var ask = ReviewPolicy.ShouldAsk(Now.AddDays(-8), Now, 60, null, 0, isQuietHours: false);

        // Assert
        ask.Should().BeTrue();
    }

    [Theory]
    [InlineData(3, 60, false)]
    [InlineData(10, 20, false)]
    [InlineData(10, 60, true)]
    public void ShouldAsk_FirstWeekFewEntriesOrNight_DoesNotAsk(int daysUsed, int entries, bool quiet)
    {
        // Act
        var ask = ReviewPolicy.ShouldAsk(Now.AddDays(-daysUsed), Now, entries, null, 0, quiet);

        // Assert
        ask.Should().BeFalse();
    }

    [Fact]
    public void ShouldAsk_AskedRecentlyOrThreeTimes_DoesNotAskAgain()
    {
        // Act
        var recently = ReviewPolicy.ShouldAsk(Now.AddDays(-200), Now, 500, Now.AddDays(-30), 1, false);
        var often = ReviewPolicy.ShouldAsk(Now.AddDays(-400), Now, 500, Now.AddDays(-120), 3, false);

        // Assert
        recently.Should().BeFalse();
        often.Should().BeFalse();
    }
}
