using Coo.Api.Application.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>How an access token is checked. The same parameters serve the bearer handler and the tests.</summary>
internal static class AccessTokenValidation
{
    public static TokenValidationParameters Parameters(SigningKeyRing keys, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(time);
        return new TokenValidationParameters
        {
            ValidIssuer = JwtTokenIssuer.Issuer,
            ValidAudience = JwtTokenIssuer.Audience,
            IssuerSigningKeys = keys.Keys,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,

            // The token's kid must name a configured key: a token signed with any other key is refused.
            TryAllIssuerSigningKeys = false,
            ClockSkew = TimeSpan.FromSeconds(30),

            // The injected clock, not the machine's (ADR 0001 §2).
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is { } end && end > now - parameters.ClockSkew
                    && (notBefore is not { } start || start <= now + parameters.ClockSkew);
            },
            NameClaimType = AccessTokenClaims.User,
        };
    }
}
