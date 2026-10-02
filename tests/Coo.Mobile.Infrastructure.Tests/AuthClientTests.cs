using System.Net;
using System.Text;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Infrastructure.Auth;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>How HTTP answers become results (Spec 010 §3.3): only a refusal is a refusal; anything else is "try later".</summary>
public sealed class AuthClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RefreshAsync_ServerBusyOrDown_ThrowsUnavailableNotRejected(HttpStatusCode status)
    {
        // Arrange: the rate limit, or a deploy restarting the API, must never sign anyone out.
        var client = Client(_ => Json(status, """{"code":"auth.rate-limited"}"""));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
    }

    [Fact]
    public async Task RefreshAsync_RefreshInvalid_ThrowsRejectedWithTheCode()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.Unauthorized, """{"code":"auth.refresh-invalid"}"""));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<AuthRejectedException>()).Which.Code.Should().Be("auth.refresh-invalid");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "http-400")]
    [InlineData(HttpStatusCode.Unauthorized, "http-401")]
    public async Task RefreshAsync_RefusalWithoutABody_ThrowsRejectedWithTheStatusAsCode(HttpStatusCode status, string code)
    {
        // Arrange: the server's bare 400 (malformed body) and the bearer handler's 401 carry no body.
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(status)));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<AuthRejectedException>()).Which.Code.Should().Be(code);
    }

    [Fact]
    public async Task GetNonceAsync_NoNetwork_ThrowsUnavailable()
    {
        // Arrange
        var client = Client(_ => throw new HttpRequestException("Network is unreachable"));

        // Act
        Func<Task> act = async () => await client.GetNonceAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
    }

    [Fact]
    public async Task SignInWithGoogleAsync_Joined_SendsTheRequestAndMapsTheGrant()
    {
        // Arrange
        string? sent = null;
        var client = Client(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK,
                """
                {"accessToken":"a","accessTokenExpiresAt":"2026-10-02T12:15:00+00:00","refreshToken":"r",
                 "user":{"id":"7a1c0000-0000-7000-8000-0000000000c1","email":"andrzej@example.com","displayName":"Andrzej"},
                 "householdId":"7a1c0000-0000-7000-8000-0000000000a9","outcome":"joined"}
                """);
        });
        var request = new GoogleSignIn("id-token", "Pixel 10",
            new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")), new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")));

        // Act
        var grant = await client.SignInWithGoogleAsync(request, CancellationToken.None);

        // Assert
        sent.Should().Be("""{"idToken":"id-token","deviceName":"Pixel 10","deviceId":"7a1c0000-0000-7000-8000-0000000000d1","provisionalHouseholdId":"7a1c0000-0000-7000-8000-0000000000a1"}""");
        grant.Outcome.Should().Be(SignInOutcome.Joined);
        grant.Household.Should().Be(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9")));
        grant.Profile.Should().Be(new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com"));
        grant.Tokens.Should().Be(new SessionTokens("a", new DateTimeOffset(2026, 10, 2, 12, 15, 0, TimeSpan.Zero), "r"));
    }

    private static AuthClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new HttpClient(new FakeServer(server)) { BaseAddress = new Uri("https://api.test/") });

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
