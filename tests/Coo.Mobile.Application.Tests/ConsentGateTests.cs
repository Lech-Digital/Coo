using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §3: consent per account, cached on the phone, read from the server so a second phone does not ask.</summary>
public sealed class ConsentGateTests
{
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly AccountProfile Profile = new(User, "Andrzej", "andrzej@example.com");

    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IConsentApi _api = Substitute.For<IConsentApi>();

    private ConsentGate Gate() => new(_state, _accounts, _api);

    [Fact]
    public async Task CheckAsync_CachedAsGiven_DoesNotAskTheServer()
    {
        // Arrange
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);

        // Act
        var state = await Gate().CheckAsync(User, CancellationToken.None);

        // Assert
        state.Should().Be(ConsentState.Given);
        await _accounts.DidNotReceiveWithAnyArgs().GetMeAsync(default);
    }

    [Fact]
    public async Task CheckAsync_UnknownHereButGivenOnTheServer_CachesItAsGiven()
    {
        // Arrange: the account's second phone (Spec 011 §3).
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Unknown);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(
            new AccountOverview(Profile, null, null, [], new ConsentInfo(CareDataConsent.CurrentWording, DateTimeOffset.UnixEpoch)));

        // Act
        var state = await Gate().CheckAsync(User, CancellationToken.None);

        // Assert
        state.Should().Be(ConsentState.Given);
        await _state.Received(1).SetConsentAsync(User, ConsentState.Given, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_ServerUnreachable_StaysUnknown()
    {
        // Arrange
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Unknown);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var state = await Gate().CheckAsync(User, CancellationToken.None);

        // Assert
        state.Should().Be(ConsentState.Unknown);
    }

    [Fact]
    public async Task AgreeAsync_ServerRecordsIt_CachesGiven()
    {
        // Act
        var agreed = await Gate().AgreeAsync(User, "0.11.0", "pl", CancellationToken.None);

        // Assert
        agreed.Should().BeTrue();
        await _api.Received(1).RecordAsync("0.11.0", "pl", Arg.Any<CancellationToken>());
        await _state.Received(1).SetConsentAsync(User, ConsentState.Given, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgreeAsync_Offline_ReturnsFalseAndCachesNothing()
    {
        // Arrange
        _api.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("offline"));

        // Act
        var agreed = await Gate().AgreeAsync(User, "0.11.0", "pl", CancellationToken.None);

        // Assert
        agreed.Should().BeFalse();
        await _state.DidNotReceiveWithAnyArgs().SetConsentAsync(default, default, default);
    }

    [Fact]
    public async Task ResetDeclinedAsync_Declined_BecomesUnknownSoTheSheetAsksAgain()
    {
        // Arrange
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);

        // Act
        await Gate().ResetDeclinedAsync(User, CancellationToken.None);

        // Assert
        await _state.Received(1).SetConsentAsync(User, ConsentState.Unknown, Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task CheckAsync_TheServerAnswersForAnotherAccount_DoesNotCacheIt()
    {
        // Arrange: the account changed while /api/me was in flight (final review M1).
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Unknown);
        var other = new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2")), "Ola", "ola@example.com");
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(
            new AccountOverview(other, null, null, [], new ConsentInfo(CareDataConsent.CurrentWording, DateTimeOffset.UnixEpoch)));

        // Act
        var state = await Gate().CheckAsync(User, CancellationToken.None);

        // Assert
        state.Should().Be(ConsentState.Unknown);
        await _state.DidNotReceiveWithAnyArgs().SetConsentAsync(default, default, default);
    }
}
