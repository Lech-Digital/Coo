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
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", new string('n', 43), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
    }

    [Fact]
    public async Task RefreshAsync_RefreshInvalid_ThrowsRejectedWithTheCode()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.Unauthorized, """{"code":"auth.refresh-invalid"}"""));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", new string('n', 43), CancellationToken.None);

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
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", new string('n', 43), CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<AuthRejectedException>()).Which.Code.Should().Be(code);
    }

    [Theory]
    [InlineData("text/html", "utf-8", "<html><body>Sign in to the Wi-Fi</body></html>")]
    [InlineData("text/html", "no-such-charset", "<html><body>Sign in to the Wi-Fi</body></html>")]
    [InlineData("application/json", "utf-8", "")]
    public async Task GetNonceAsync_OkWithAnUnreadableBody_ThrowsUnavailable(string mediaType, string charset, string body)
    {
        // Arrange: a captive portal answering 200 with its own page; no exception but "try later" may come out.
        var client = Client(_ => Raw(HttpStatusCode.OK, mediaType, charset, body));

        // Act
        Func<Task> act = async () => await client.GetNonceAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<AuthUnavailableException>();
    }

    [Fact]
    public async Task RefreshAsync_OkWithAPortalPage_ThrowsUnavailable()
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.OK, "text/html", "utf-8", "<html>Sign in to the Wi-Fi</html>"));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", new string('n', 43), CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<AuthUnavailableException>();
    }

    [Fact]
    public async Task RefreshAsync_RefusalWithAnUnreadableBody_ThrowsRejectedWithTheStatusAsCode()
    {
        // Arrange: a portal's 403 whose charset cannot even be decoded.
        var client = Client(_ => Raw(HttpStatusCode.Forbidden, "text/html", "no-such-charset", "<html>Blocked</html>"));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", new string('n', 43), CancellationToken.None);

        // Assert
        (await act.Should().ThrowExactlyAsync<AuthRejectedException>()).Which.Code.Should().Be("http-403");
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

    [Fact]
    public async Task RefreshAsync_Proposal_TravelsInTheBody()
    {
        // Arrange
        string? body = null;
        var client = Client(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, """{"accessToken":"a","accessTokenExpiresAt":"2026-10-02T12:15:00Z","refreshToken":"nnn"}""");
        });

        // Act
        await client.RefreshAsync("refresh-1", "nnn", CancellationToken.None);

        // Assert
        body.Should().Contain("\"refreshToken\":\"refresh-1\"").And.Contain("\"nextRefreshToken\":\"nnn\"");
    }

    [Fact]
    public async Task GetMeAsync_WithConsent_MapsTheWordingAndTime()
    {
        // Arrange
        var client = Account(_ => Json(HttpStatusCode.OK, MeBody("""{"wording":"care-data-v1","givenAt":"2026-10-02T12:00:00+00:00"}""")));

        // Act
        var overview = await client.GetMeAsync(CancellationToken.None);

        // Assert
        overview.Consent.Should().Be(new ConsentInfo("care-data-v1", new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task GetMeAsync_WithoutConsent_HasNone(string? consent)
    {
        // Arrange: a 0.10.0 server sends no field at all.
        var client = Account(_ => Json(HttpStatusCode.OK, MeBody(consent)));

        // Act
        var overview = await client.GetMeAsync(CancellationToken.None);

        // Assert
        overview.Consent.Should().BeNull();
    }

    [Fact]
    public async Task GetMeAsync_Memberships_MapsEachActiveFamily()
    {
        // Arrange
        var client = new AccountClient(new HttpClient(new FakeServer(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"user":{"id":"7a1c0000-0000-7000-8000-0000000000c1","email":"k@example.com","displayName":"Kasia"},
                 "household":{"householdId":"7a1c0000-0000-7000-8000-0000000000a1","role":"owner"},"devices":[],
                 "memberships":[{"householdId":"7a1c0000-0000-7000-8000-0000000000a1","role":"owner","joinedAt":"2026-09-01T00:00:00+00:00"},
                                {"householdId":"7a1c0000-0000-7000-8000-0000000000a2","role":"parent","joinedAt":"2026-10-06T00:00:00+00:00"},
                                {"householdId":"7a1c0000-0000-7000-8000-0000000000a3","role":"admin","joinedAt":"2026-10-06T00:00:00+00:00"}]}
                """, Encoding.UTF8, "application/json"),
        })))
        { BaseAddress = new Uri("https://api.test/") });

        // Act
        var me = await client.GetMeAsync(CancellationToken.None);

        // Assert: a role this build does not know is left out, not guessed.
        me.Memberships!.Select(m => (m.Household.Value.ToString(), m.Role)).Should().Equal(
            ("7a1c0000-0000-7000-8000-0000000000a1", Coo.Domain.Households.Role.Owner), ("7a1c0000-0000-7000-8000-0000000000a2", Coo.Domain.Households.Role.Parent));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(",\"memberships\":[]", 0)]
    public async Task GetMeAsync_MembershipsFieldMissingOrEmpty_IsUnknownOrEmpty(string field, int? count)
    {
        // Arrange
        var body = """{"user":{"id":"7a1c0000-0000-7000-8000-0000000000c1","email":"a@b.c","displayName":"A"},"household":null,"devices":[]""" + field + "}";
        var client = Account(_ => Json(HttpStatusCode.OK, body));

        // Act
        var me = await client.GetMeAsync(CancellationToken.None);

        // Assert
        me.Memberships?.Count.Should().Be(count);
        (me.Memberships is null).Should().Be(count is null);
    }

    private static string MeBody(string? consent) =>
        """{"user":{"id":"7a1c0000-0000-7000-8000-0000000000c1","email":"a@b.c","displayName":"A"},"household":null,"devices":[]""" +
        (consent is null ? "" : ",\"consent\":" + consent) + "}";

    private static AccountClient Account(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new HttpClient(new FakeServer(server)) { BaseAddress = new Uri("https://api.test/") });

    private static AuthClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new HttpClient(new FakeServer(server)) { BaseAddress = new Uri("https://api.test/") });

    private static Task<HttpResponseMessage> Raw(HttpStatusCode status, string mediaType, string charset, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) { CharSet = charset };
        return Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
