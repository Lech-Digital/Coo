using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// The access token (ADR 0003 §4): a JWT, HMAC-SHA256, 15 minutes. Its claims are the user and the device session; the
/// key ID is in the header. No household claim: membership is checked per request.
/// </summary>
internal sealed class JwtTokenIssuer(SigningKeyRing keys) : ITokenIssuer
{
    public const string Issuer = "coo-api";
    public const string Audience = "coo-app";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(UserId user, DeviceSessionId session, DateTimeOffset now)
    {
        var expires = now + Lifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [AccessTokenClaims.User] = user.Value.ToString("D", CultureInfo.InvariantCulture),
                [AccessTokenClaims.Session] = session.Value.ToString("D", CultureInfo.InvariantCulture),
            },
            SigningCredentials = keys.Active,
        });
        return new AccessToken(token, expires);
    }
}
