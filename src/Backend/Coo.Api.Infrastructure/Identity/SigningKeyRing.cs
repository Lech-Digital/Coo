using Microsoft.IdentityModel.Tokens;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// The HMAC-SHA256 keys (ADR 0003 §4): one or two, each with its ID. One signs; all of them validate, so a key can be
/// rotated without signing anyone out. The host has already checked the sizes (StartupChecks.SelectAuth).
/// </summary>
internal sealed class SigningKeyRing
{
    public SigningKeyRing(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Keys = [.. keys.Select(k => (SecurityKey)new SymmetricSecurityKey(k.Value) { KeyId = k.Key })];
        var active = Keys.SingleOrDefault(k => k.KeyId == activeKeyId)
            ?? throw new ArgumentException($"The active signing key '{activeKeyId}' is not configured.", nameof(activeKeyId));
        Active = new SigningCredentials(active, SecurityAlgorithms.HmacSha256);
    }

    public IReadOnlyList<SecurityKey> Keys { get; }

    public SigningCredentials Active { get; }
}
