using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Coo.Api.Application.Auth;

/// <summary>Nonces and refresh tokens (ADR 0003 §4): 256 random bits; the database keeps only their SHA-256.</summary>
public static class AuthSecrets
{
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}
