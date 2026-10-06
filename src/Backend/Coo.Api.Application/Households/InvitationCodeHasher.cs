using System.Security.Cryptography;
using System.Text;

namespace Coo.Api.Application.Households;

/// <summary>
/// An invitation code's stored hash (Spec 011b §10.3): HMAC-SHA256 under a pepper that comes from configuration and never
/// reaches the database. A code has only 40 bits, so an unkeyed hash in a leaked database could be brute-forced; with the
/// pepper the database alone is not enough. Changing the pepper invalidates every open invitation.
/// </summary>
public sealed class InvitationCodeHasher
{
    private readonly byte[] _pepper;

    public InvitationCodeHasher(byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length == 0)
            throw new ArgumentException("The pepper must not be empty.", nameof(pepper));
        _pepper = [.. pepper];
    }

    /// <param name="code">The 8 characters, capitals, no hyphen.</param>
    public string Hash(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(code)));
    }
}
