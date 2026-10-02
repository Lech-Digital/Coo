using System.Net;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Infrastructure.Auth;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 010 §3.3: one refresh at a time; a network failure is "try later"; a refused refresh signs out.</summary>
public sealed class AuthenticatingHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Me = new("api/me", UriKind.Relative);
    private static readonly StoredSession Session = new(
        "old", Now.AddMinutes(10), "refresh-1",
        new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com"),
        new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")), JoinPending: false);

    private readonly SessionStore _sessions = new(new MemorySecrets());
    private readonly IAuthApi _api = Substitute.For<IAuthApi>();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly Account _account;
    private readonly TokenRefresher _refresher;

    public AuthenticatingHandlerTests()
    {
        _account = new Account(_sessions);
        _refresher = new TokenRefresher(_sessions, _api, _account, _time);
    }

    public void Dispose()
    {
        _refresher.Dispose();
        _account.Dispose();
    }

    [Fact]
    public async Task SendAsync_ValidToken_SendsItAsBearerWithoutRefreshing()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        string? sentWith = null;
        using var client = Client(r =>
        {
            sentWith = r.Headers.Authorization?.ToString();
            return Ok();
        });

        // Act
        var response = await client.GetAsync(Me);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sentWith.Should().Be("Bearer old");
        await _api.DidNotReceiveWithAnyArgs().RefreshAsync(default!, default!, default);
    }

    [Fact]
    public async Task SendAsync_ManyConcurrent401s_RefreshesOnce()
    {
        // Arrange: ten calls with the expired token; the refresh is held until all ten have been refused.
        await _account.SignedInAsync(Session, CancellationToken.None);
        var refused = 0;
        var allRefused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await allRefused.Task;
            return new SessionTokens("new", Now.AddMinutes(15), "refresh-2");
        });
        using var client = Client(r =>
        {
            if (r.Headers.Authorization?.Parameter != "old")
                return Ok();
            if (Interlocked.Increment(ref refused) == 10)
                allRefused.SetResult();
            return Status(HttpStatusCode.Unauthorized);
        });

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetAsync(Me)));

        // Assert
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        await _api.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await _sessions.GetAsync(CancellationToken.None))!.RefreshToken.Should().Be("refresh-2");
    }

    [Fact]
    public async Task SendAsync_TokenAboutToExpire_RefreshesBeforeSending()
    {
        // Arrange: 30 seconds left, less than the one-minute margin.
        await _account.SignedInAsync(Session with { AccessTokenExpiresAt = Now.AddSeconds(30) }, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        var tokens = new List<string?>();
        using var client = Client(r =>
        {
            tokens.Add(r.Headers.Authorization?.Parameter);
            return Ok();
        });

        // Act
        await client.GetAsync(Me);

        // Assert
        tokens.Should().Equal("new");
    }

    [Fact]
    public async Task SendAsync_RefreshUnreachable_KeepsTheSessionAndThrows()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));
        using var client = Client(_ => Status(HttpStatusCode.Unauthorized));

        // Act
        Func<Task> act = async () => await client.GetAsync(Me);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
        (await StoredApartFromTheProposalAsync()).Should().Be(Session);
        _account.State.Should().Be(new SignedIn(Session.Profile));
    }

    [Fact]
    public async Task RefreshAsync_CallersJoiningAFailingRefresh_ShareItsOneAttempt()
    {
        // Arrange: every caller is waiting before the refresh fails, so none of them starts a second one.
        await _account.SignedInAsync(Session, CancellationToken.None);
        var answer = new TaskCompletionSource<SessionTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var waiting = Enumerable.Range(0, 5).Select(_ => Record(() => _refresher.RefreshAsync("old", CancellationToken.None))).ToList();

        // Act
        answer.SetException(new AuthUnavailableException("offline"));
        var outcomes = await Task.WhenAll(waiting);

        // Assert
        outcomes.Should().OnlyContain(e => e is AuthUnavailableException);
        await _api.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await StoredApartFromTheProposalAsync()).Should().Be(Session);
    }

    [Fact]
    public async Task RefreshAsync_AfterAFailedAttempt_TriesAgain()
    {
        // Arrange: a failure is not remembered; the next call is a new attempt.
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            _ => throw new AuthUnavailableException("offline"),
            _ => Task.FromResult(new SessionTokens("new", Now.AddMinutes(15), "refresh-2")));
        Func<Task> first = async () => await _refresher.RefreshAsync("old", CancellationToken.None);
        await first.Should().ThrowAsync<AuthUnavailableException>();

        // Act
        var token = await _refresher.RefreshAsync("old", CancellationToken.None);

        // Assert
        token.Should().Be("new");
        await _api.Received(2).RefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_RefreshRejected_SignsOutAsExpiredAndReturns401()
    {
        // Arrange: the session was revoked on the server. Only the tokens go; local data is not this code's to touch.
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthRejectedException("auth.refresh-invalid"));
        using var client = Client(_ => Status(HttpStatusCode.Unauthorized));

        // Act
        var response = await client.GetAsync(Me);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _account.State.Should().Be(new SignedOut(SessionExpired: true));
        (await _sessions.GetAsync(CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("http-403")]
    [InlineData("http-404")]
    [InlineData("http-407")]
    [InlineData("auth.google-token-invalid")]
    public async Task RefreshAsync_RefusedWithAnotherCode_KeepsTheSessionAndThrowsUnavailable(string code)
    {
        // Arrange: a captive portal or a misrouted proxy during a deploy; only auth.refresh-invalid ends a session.
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthRejectedException(code));

        // Act
        Func<Task> act = async () => await _refresher.RefreshAsync("old", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
        (await StoredApartFromTheProposalAsync()).Should().Be(Session);
        _account.State.Should().Be(new SignedIn(Session.Profile));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task SendAsync_RefreshAnsweredForbiddenOrTimeout_KeepsTheSessionAndThrowsUnavailable(HttpStatusCode status)
    {
        // Arrange: the real client in front of the refresh, the server's /refresh answering a portal's 403 or a 408.
        using var authHttp = new HttpClient(new FakeServer(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("<html>Sign in to the Wi-Fi</html>", System.Text.Encoding.UTF8, "text/html"),
        })))
        { BaseAddress = new Uri("https://api.test/") };
        using var refresher = new TokenRefresher(_sessions, new AuthClient(authHttp), _account, _time);
        await _account.SignedInAsync(Session, CancellationToken.None);
        using var client = new HttpClient(new AuthenticatingHandler(refresher) { InnerHandler = new FakeServer(_ => Status(HttpStatusCode.Unauthorized)) })
        { BaseAddress = new Uri("https://api.test/") };

        // Act
        Func<Task> act = async () => await client.GetAsync(Me);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
        (await StoredApartFromTheProposalAsync()).Should().Be(Session);
        _account.State.Should().Be(new SignedIn(Session.Profile));
    }

    [Fact]
    public async Task SendAsync_401OnARequestWithABody_RetriesWithTheSameBody()
    {
        // Arrange: DELETE /api/account { "confirm": true } with a token that just expired.
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        var bodies = new List<string>();
        using var client = Client(async r =>
        {
            bodies.Add(await r.Content!.ReadAsStringAsync());
            return r.Headers.Authorization?.Parameter == "old" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("api/account", UriKind.Relative))
        {
            Content = new StringContent("""{"confirm":true}""", System.Text.Encoding.UTF8, "application/json"),
        };

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        bodies.Should().Equal("""{"confirm":true}""", """{"confirm":true}""");
    }

    [Fact]
    public async Task SendAsync_SignedOut_Returns401WithoutCallingTheServer()
    {
        // Arrange
        var server = new FakeServer(_ => Ok());
        using var client = new HttpClient(new AuthenticatingHandler(_refresher) { InnerHandler = server }) { BaseAddress = new Uri("https://api.test/") };

        // Act
        var response = await client.GetAsync(Me);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        server.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_FirstAttemptUnavailable_SendsTheSameProposalAgain()
    {
        // Arrange: the response to the first refresh is lost; the retry must carry the same pair (Spec 011 §8.2).
        await _account.SignedInAsync(Session, CancellationToken.None);
        var proposals = new List<string>();
        _api.RefreshAsync("refresh-1", Arg.Do<string>(proposals.Add), Arg.Any<CancellationToken>())
            .Returns(_ => throw new AuthUnavailableException("lost"), _ => Task.FromResult(new SessionTokens("new", Now.AddMinutes(15), proposals[^1])));

        // Act
        var first = await Record(() => _refresher.RefreshAsync("old", CancellationToken.None));
        var token = await _refresher.RefreshAsync("old", CancellationToken.None);

        // Assert
        first.Should().BeOfType<AuthUnavailableException>();
        token.Should().Be("new");
        proposals.Should().HaveCount(2);
        proposals[1].Should().Be(proposals[0]);
        proposals[0].Should().HaveLength(43);
        (await _sessions.GetAsync(CancellationToken.None))!.Should().Match<StoredSession>(s => s.RefreshToken == proposals[0] && s.PendingRefreshToken == null);
    }

    [Fact]
    public async Task RefreshAsync_SignedOutWhileTheRefreshIsInFlight_StaysSignedOut()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var answer = new TaskCompletionSource<SessionTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var refresh = _refresher.RefreshAsync("old", CancellationToken.None);
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        answer.SetResult(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        var token = await refresh;

        // Assert
        token.Should().BeNull();
        (await _sessions.GetAsync(CancellationToken.None)).Should().BeNull();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task RefreshAsync_RefusedAfterADeliberateSignOut_DoesNotSayExpired()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var answer = new TaskCompletionSource<SessionTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var refresh = _refresher.RefreshAsync("old", CancellationToken.None);
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        answer.SetException(new AuthRejectedException("auth.refresh-invalid"));
        var token = await refresh;

        // Assert
        token.Should().BeNull();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task RefreshAsync_SignedInAgainWhileInFlight_KeepsTheNewSession()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var answer = new TaskCompletionSource<SessionTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var refresh = _refresher.RefreshAsync("old", CancellationToken.None);
        var again = Session with { AccessToken = "access-9", RefreshToken = "refresh-9" };
        await _account.SignedInAsync(again, CancellationToken.None);

        // Act
        answer.SetResult(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        await refresh;

        // Assert
        (await _sessions.GetAsync(CancellationToken.None)).Should().Be(again);
    }

    /// <summary>The stored session without its proposed next refresh token, which a failed refresh keeps for the retry (Spec 011 §8.2).</summary>
    private async Task<StoredSession?> StoredApartFromTheProposalAsync() =>
        await _sessions.GetAsync(CancellationToken.None) is { } stored ? stored with { PendingRefreshToken = null } : null;

    private HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new AuthenticatingHandler(_refresher) { InnerHandler = new FakeServer(server) }) { BaseAddress = new Uri("https://api.test/") };

    /// <summary>What the call threw, or null; started at once, so each caller is waiting before the test moves on.</summary>
    private static async Task<Exception?> Record<T>(Func<Task<T>> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static Task<HttpResponseMessage> Ok() => Status(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> Status(HttpStatusCode status) => Task.FromResult(new HttpResponseMessage(status));
}
