using System.Globalization;
using System.Security.Claims;
using Coo.Api.Application.Auth;
using Coo.Api.Infrastructure.Identity;
using Coo.Domain.Common;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 010 §3.3 and §7: the real issuer and validator, two key IDs, an unknown key; the verifier fails closed.</summary>
public sealed class TokenTests
{
    private const string ClientId = "client.apps.googleusercontent.com";
    private static readonly byte[] KeyOne = [.. Enumerable.Repeat((byte)1, 32)];
    private static readonly byte[] KeyTwo = [.. Enumerable.Repeat((byte)2, 32)];
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c1"));
    private static readonly DeviceSessionId Session = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0f1"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Validate_TokensFromEitherOfTwoKeys_AreAcceptedWithTheirClaims()
    {
        // Arrange: k1 signed before the rotation, k2 signs now; both stay valid while both are configured.
        var beforeRotation = new JwtTokenIssuer(Ring("k1", ("k1", KeyOne)));
        var afterRotation = new JwtTokenIssuer(Ring("k2", ("k1", KeyOne), ("k2", KeyTwo)));
        var oldToken = beforeRotation.Issue(User, Session, _time.GetUtcNow());
        var newToken = afterRotation.Issue(User, Session, _time.GetUtcNow());
        var parameters = AccessTokenValidation.Parameters(Ring("k2", ("k1", KeyOne), ("k2", KeyTwo)), _time);

        // Act
        var results = new[] { await ValidateAsync(oldToken.Value, parameters), await ValidateAsync(newToken.Value, parameters) };

        // Assert
        results.Should().OnlyContain(r => r.IsValid);
        results[1].Claims[AccessTokenClaims.User].Should().Be(User.Value.ToString("D", CultureInfo.InvariantCulture));
        results[1].Claims[AccessTokenClaims.Session].Should().Be(Session.Value.ToString("D", CultureInfo.InvariantCulture));
        new JsonWebToken(oldToken.Value).Kid.Should().Be("k1");
        new JsonWebToken(newToken.Value).Kid.Should().Be("k2");
        newToken.ExpiresAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Validate_TokenSignedWithAnUnknownKey_IsRefused()
    {
        // Arrange
        var stranger = new JwtTokenIssuer(Ring("k9", ("k9", KeyTwo)));
        var token = stranger.Issue(User, Session, _time.GetUtcNow());

        // Act
        var result = await ValidateAsync(token.Value, AccessTokenValidation.Parameters(Ring("k1", ("k1", KeyOne)), _time));

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_KnownKeyIdWithTheWrongSecret_IsRefused()
    {
        // Arrange: a forger who guessed the key ID but not the key.
        var forger = new JwtTokenIssuer(Ring("k1", ("k1", KeyTwo)));
        var token = forger.Issue(User, Session, _time.GetUtcNow());

        // Act
        var result = await ValidateAsync(token.Value, AccessTokenValidation.Parameters(Ring("k1", ("k1", KeyOne)), _time));

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_After15MinutesAndTheSkew_IsRefused()
    {
        // Arrange
        var issuer = new JwtTokenIssuer(Ring("k1", ("k1", KeyOne)));
        var token = issuer.Issue(User, Session, _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(31));

        // Act
        var result = await ValidateAsync(token.Value, AccessTokenValidation.Parameters(Ring("k1", ("k1", KeyOne)), _time));

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task OnTokenValidated_ActiveSession_IsAccepted()
    {
        // Arrange
        var sessions = Substitute.For<IDeviceSessionStore>();
        sessions.IsActiveAsync(Session, Arg.Any<CancellationToken>()).Returns(true);
        var context = TokenValidatedContextFor(sessions, Session.Value.ToString("D", CultureInfo.InvariantCulture), out var options);

        // Act
        await options.Events.TokenValidated(context);

        // Assert
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task OnTokenValidated_SignedOutOrReplacedSession_IsRefused()
    {
        // Arrange: the token is still cryptographically valid, but its session was revoked (Review Focus 1).
        var sessions = Substitute.For<IDeviceSessionStore>();
        sessions.IsActiveAsync(Session, Arg.Any<CancellationToken>()).Returns(false);
        var context = TokenValidatedContextFor(sessions, Session.Value.ToString("D", CultureInfo.InvariantCulture), out var options);

        // Act
        await options.Events.TokenValidated(context);

        // Assert
        context.Result.Should().NotBeNull();
        context.Result!.Failure.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task OnTokenValidated_NoUsableSessionClaim_IsRefusedWithoutAskingTheStore(string? claim)
    {
        // Arrange
        var sessions = Substitute.For<IDeviceSessionStore>();
        var context = TokenValidatedContextFor(sessions, claim, out var options);

        // Act
        await options.Events.TokenValidated(context);

        // Assert
        context.Result!.Failure.Should().NotBeNull();
        await sessions.DidNotReceiveWithAnyArgs().IsActiveAsync(default, default);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("")]
    public async Task VerifyAsync_GarbageToken_ReturnsNull(string token)
    {
        // Arrange
        var verifier = new GoogleIdTokenVerifier(ClientId, _time, NullLogger<GoogleIdTokenVerifier>.Instance);

        // Act
        var identity = await verifier.VerifyAsync(token, CancellationToken.None);

        // Assert
        identity.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_NoClientIdConfigured_RefusesEverything()
    {
        // Arrange: outside Production without settings; never "accept everything" (ADR 0003 §5).
        var verifier = new GoogleIdTokenVerifier(null, _time, NullLogger<GoogleIdTokenVerifier>.Instance);

        // Act
        var identity = await verifier.VerifyAsync("eyJhbGciOiJSUzI1NiJ9.e30.c2ln", CancellationToken.None);

        // Assert
        identity.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_LibraryAcceptsTheToken_ReturnsTheIdentityCheckedAgainstOurClientAndClock()
    {
        // Arrange
        GoogleJsonWebSignature.ValidationSettings? seen = null;
        var verifier = VerifierWith((_, settings) =>
        {
            seen = settings;
            return Task.FromResult(Payload());
        });

        // Act
        var identity = await verifier.VerifyAsync("a.b.c", CancellationToken.None);

        // Assert
        identity.Should().Be(new ExternalIdentity("1234567890", "parent@example.com", "A Parent", "nonce-1"));
        seen!.Audience.Should().Equal(ClientId);
        seen.Clock.UtcNow.Should().Be(_time.GetUtcNow().UtcDateTime);
    }

    public static TheoryData<Exception> LibraryFailures() => new()
    {
        // Google's keys endpoint unreachable.
        new HttpRequestException("No such host is known."),

        // The keys request timed out (HttpClient reports a timeout as a cancellation the caller did not ask for).
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),

        // Malformed, expired, wrong audience or wrong signature: the library's own refusal.
        new InvalidJwtException("JWT has expired."),

        // Anything else the library or its JSON parsing might throw.
        new InvalidOperationException("Unexpected."),
    };

    [Theory]
    [MemberData(nameof(LibraryFailures))]
    public async Task VerifyAsync_LibraryThrows_ReturnsNullNeverThrows(Exception failure)
    {
        // Arrange
        var verifier = VerifierWith((_, _) => Task.FromException<GoogleJsonWebSignature.Payload>(failure));

        // Act
        Func<Task<ExternalIdentity?>> act = () => verifier.VerifyAsync("a.b.c", CancellationToken.None);

        // Assert
        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_LibraryAcceptsAnUnverifiedEmail_ReturnsNull()
    {
        // Arrange
        var payload = Payload();
        payload.EmailVerified = false;
        var verifier = VerifierWith((_, _) => Task.FromResult(payload));

        // Act
        var identity = await verifier.VerifyAsync("a.b.c", CancellationToken.None);

        // Assert
        identity.Should().BeNull();
    }

    [Fact]
    public void ToIdentity_VerifiedGooglePayload_ReturnsTheIdentity()
    {
        // Arrange
        var payload = Payload();

        // Act
        var identity = GoogleIdTokenVerifier.ToIdentity(payload);

        // Assert
        identity.Should().Be(new ExternalIdentity("1234567890", "parent@example.com", "A Parent", "nonce-1"));
    }

    [Fact]
    public void ToIdentity_EmailNotVerified_ReturnsNull()
    {
        // Arrange
        var payload = Payload();
        payload.EmailVerified = false;

        // Act
        var identity = GoogleIdTokenVerifier.ToIdentity(payload);

        // Assert
        identity.Should().BeNull();
    }

    [Theory]
    [InlineData("https://evil.example.com", "nonce-1")]
    [InlineData("https://accounts.google.com", "")]
    public void ToIdentity_WrongIssuerOrNoNonce_ReturnsNull(string issuer, string nonce)
    {
        // Arrange
        var payload = Payload();
        payload.Issuer = issuer;
        payload.Nonce = nonce;

        // Act
        var identity = GoogleIdTokenVerifier.ToIdentity(payload);

        // Assert
        identity.Should().BeNull();
    }

    private static GoogleJsonWebSignature.Payload Payload() => new()
    {
        Issuer = "https://accounts.google.com",
        Subject = "1234567890",
        Email = "parent@example.com",
        EmailVerified = true,
        Name = "A Parent",
        Nonce = "nonce-1",
    };

    private GoogleIdTokenVerifier VerifierWith(
        Func<string, GoogleJsonWebSignature.ValidationSettings, Task<GoogleJsonWebSignature.Payload>> validate) =>
        new(ClientId, _time, NullLogger<GoogleIdTokenVerifier>.Instance, validate);

    private TokenValidatedContext TokenValidatedContextFor(IDeviceSessionStore sessions, string? sessionClaim, out JwtBearerOptions options)
    {
        options = new JwtBearerOptions();
        new JwtBearerSetup(Ring("k1", ("k1", KeyOne)), _time, sessions).Configure(JwtBearerDefaults.AuthenticationScheme, options);
        var claims = new List<Claim> { new(AccessTokenClaims.User, User.Value.ToString("D", CultureInfo.InvariantCulture)) };
        if (sessionClaim is not null)
            claims.Add(new Claim(AccessTokenClaims.Session, sessionClaim));
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        return new TokenValidatedContext(new DefaultHttpContext(), scheme, options)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme)),
        };
    }

    private static SigningKeyRing Ring(string active, params (string Id, byte[] Key)[] keys) =>
        new(active, keys.ToDictionary(k => k.Id, k => k.Key, StringComparer.Ordinal));

    private static Task<TokenValidationResult> ValidateAsync(string token, TokenValidationParameters parameters) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
}
