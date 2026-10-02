using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Coo.Api.Application.Auth;
using Coo.Contracts.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Coo.Api.IntegrationTests;

/// <summary>A Google that verifies "subject|email|nonce" and nothing else. The real one cannot run in a test.</summary>
internal sealed class FakeGoogle : IExternalIdentityVerifier
{
    public static string Token(string subject, string nonce) => $"{subject}|{subject}@example.com|{nonce}";

    public Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        var parts = idToken.Split('|');
        return Task.FromResult(parts.Length == 3 ? new ExternalIdentity(parts[0], parts[1], "Parent " + parts[0], parts[2]) : null);
    }
}

/// <summary>One PostgreSQL and one API for a test class; every test uses its own subjects, phones and households.</summary>
public sealed class AuthApi : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    internal FakeGoogle Google { get; } = new();

    public async Task InitializeAsync()
    {
        try
        {
            await _postgres.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL tests need a running Docker: start Docker Desktop (Spec 008 §9). CI has Docker.", e);
        }

        Factory = TestApi.Create(ConnectionString, Google);
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

internal static class Calls
{
    public static readonly Uri Nonce = new("/api/auth/nonce", UriKind.Relative);
    public static readonly Uri Google = new("/api/auth/google", UriKind.Relative);
    public static readonly Uri Refresh = new("/api/auth/refresh", UriKind.Relative);
    public static readonly Uri SignOut = new("/api/auth/sign-out", UriKind.Relative);
    public static readonly Uri Me = new("/api/me", UriKind.Relative);
    public static readonly Uri Account = new("/api/account", UriKind.Relative);

    /// <summary>What the phone does: a nonce, a Google token carrying it, then <c>/api/auth/google</c>.</summary>
    public static async Task<SessionResponse> SignInAsync(HttpClient client, string subject, Guid device, Guid household)
    {
        var nonce = await (await client.PostAsync(Nonce, null)).Content.ReadFromJsonAsync<NonceResponse>();
        var response = await client.PostAsJsonAsync(Google, new GoogleSignInRequest(FakeGoogle.Token(subject, nonce!.Nonce), "Pixel 10", device, household));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
    }

    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code;

    public static Guid Id(int n) => Guid.Parse("7a1c0000-0000-7000-8000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Spec 010 §4.1 and §8, end to end with a fake Google.</summary>
public sealed class AuthApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task SignIn_NewUser_AdoptsThePhonesHouseholdAndMeShowsTheOwner()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var household = Calls.Id(101);

        // Act
        var session = await Calls.SignInAsync(client, "sub-adopt", Calls.Id(1), household);
        var me = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Assert
        session.Outcome.Should().Be(AuthOutcomes.Adopted);
        session.HouseholdId.Should().Be(household);
        session.User.Email.Should().Be("sub-adopt@example.com");
        me!.Household.Should().Be(new MembershipDto(household, AuthRoles.Owner));
        me.Devices.Should().ContainSingle().Which.Should().Match<DeviceSessionDto>(d => d.IsCurrent && d.DeviceName == "Pixel 10");
    }

    [Fact]
    public async Task SignIn_SameAccountOnASecondPhone_IsJoinedAndMeShowsOneHouseholdAndTwoDevices()
    {
        // Arrange: deliverable 3.
        using var client = api.Factory.CreateClient();
        var first = await Calls.SignInAsync(client, "sub-join", Calls.Id(2), Calls.Id(102));

        // Act
        var second = await Calls.SignInAsync(client, "sub-join", Calls.Id(3), Calls.Id(103));
        var me = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, second.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Assert
        second.Outcome.Should().Be(AuthOutcomes.Joined);
        second.HouseholdId.Should().Be(first.HouseholdId);
        me!.Household!.HouseholdId.Should().Be(Calls.Id(102));
        me.Devices.Should().HaveCount(2);
    }

    [Fact]
    public async Task Me_WithoutAToken_Returns401()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, accessToken: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("the bearer handler's default: no body");
    }

    [Fact]
    public async Task Google_TokenGoogleDidNotVerify_Returns401WithTheCode()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(Calls.Google, new GoogleSignInRequest("forged", "Pixel 10", Calls.Id(4), Calls.Id(104)));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.GoogleTokenInvalid);
    }

    [Fact]
    public async Task Google_EmptyDeviceId_Returns401WithTheCode()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var nonce = await (await client.PostAsync(Calls.Nonce, null)).Content.ReadFromJsonAsync<NonceResponse>();

        // Act
        var response = await client.PostAsJsonAsync(Calls.Google,
            new GoogleSignInRequest(FakeGoogle.Token("sub-empty", nonce!.Nonce), "Pixel 10", Guid.Empty, Calls.Id(105)));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.GoogleTokenInvalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("""{"deviceName":"Pixel 10","deviceId":"7a1c0000-0000-7000-8000-000000000005","provisionalHouseholdId":"7a1c0000-0000-7000-8000-000000000105"}""")]
    [InlineData("""{"idToken":"","deviceName":"Pixel 10","deviceId":"7a1c0000-0000-7000-8000-000000000005","provisionalHouseholdId":"7a1c0000-0000-7000-8000-000000000105"}""")]
    [InlineData("""{"idToken":"a|b|c","deviceName":"Pixel 10","deviceId":"7a1c0000-0000-7000-8000-000000000005"}""")]
    [InlineData("""{"idToken":"a|b|c","deviceName":"Pixel 10","deviceId":"not-a-guid","provisionalHouseholdId":"7a1c0000-0000-7000-8000-000000000105"}""")]
    public async Task Google_MalformedRequest_Returns401WithTheCode(string body)
    {
        // Arrange: refused like a bad token, no new error code (ruling).
        using var client = api.Factory.CreateClient();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync(Calls.Google, content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.GoogleTokenInvalid);
    }

    [Fact]
    public async Task Refresh_SamePairSentTwice_SucceedsBothTimesAndKeepsTheSession()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "retry-1", Calls.Id(9101), Calls.Id(9102));
        var next = new string('t', 43);
        var first = await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken, next));

        // Act
        var retried = await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken, next));

        // Assert
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await retried.Content.ReadFromJsonAsync<TokensResponse>();
        tokens!.RefreshToken.Should().Be(next);
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, tokens.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_ReusedToken_RevokesTheSession()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-reuse", Calls.Id(6), Calls.Id(106));
        var rotated = await (await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken)))
            .Content.ReadFromJsonAsync<TokensResponse>();

        // Act
        var reused = await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken));

        // Assert
        reused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.CodeAsync(reused)).Should().Be(AuthErrors.RefreshInvalid);
        (await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(rotated!.RefreshToken))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "the session is revoked, so the phone's newer token is refused too");
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, rotated.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_AfterSignOut_RefusesTheOldAccessToken()
    {
        // Arrange: deliverable 6; the access token itself has 15 minutes left.
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-signout", Calls.Id(7), Calls.Id(107));

        // Act
        var signOut = await Calls.SendAsync(client, HttpMethod.Post, Calls.SignOut, session.AccessToken);
        var me = await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken);

        // Assert
        signOut.StatusCode.Should().Be(HttpStatusCode.NoContent);
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_AfterSignInAgainOnTheSamePhone_RefusesTheOldAccessToken()
    {
        // Arrange: signing in again on the same phone replaces its session (Review Focus 1 and 3).
        using var client = api.Factory.CreateClient();
        var first = await Calls.SignInAsync(client, "sub-again", Calls.Id(10), Calls.Id(110));

        // Act
        var second = await Calls.SignInAsync(client, "sub-again", Calls.Id(10), Calls.Id(110));
        var me = await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, first.AccessToken);

        // Assert
        second.Outcome.Should().Be(AuthOutcomes.Existing);
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, second.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteAccount_WithoutConfirmation_Returns400WithTheCodeAndKeepsTheAccount()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-noconfirm", Calls.Id(8), Calls.Id(108));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Delete, Calls.Account, session.AccessToken, new DeleteAccountRequest(false));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.ConfirmationRequired);
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteAccount_Confirmed_ForgetsTheUserAndTheHousehold()
    {
        // Arrange: deliverable 7.
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-delete", Calls.Id(9), Calls.Id(109));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Delete, Calls.Account, session.AccessToken, new DeleteAccountRequest(true));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountAsync($"""SELECT count(*) FROM "AspNetUsers" WHERE "Id" = '{session.User.Id}';""")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM households WHERE id = '{Calls.Id(109)}';")).Should().Be(0);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>Spec 010 §4.1: 20 requests per minute per client address on <c>/api/auth/*</c>.</summary>
public sealed class RateLimitTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Nonce_21stRequestInAMinute_Returns429WithTheCode()
    {
        // Arrange: the production default, not the test fixture's raised limit.
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, permitsPerMinute: null);
        using var client = factory.CreateClient();
        for (var i = 0; i < 20; i++)
            (await client.PostAsync(Calls.Nonce, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await client.PostAsync(Calls.Nonce, null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.RateLimited);
    }
}
