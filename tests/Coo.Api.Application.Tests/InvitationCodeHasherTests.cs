using System.Security.Cryptography;
using System.Text;
using Coo.Api.Application.Auth;
using Coo.Api.Application.Households;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 011b §10.3: a code's hash is an HMAC-SHA256 under a pepper that lives in configuration, not in the database.</summary>
public sealed class InvitationCodeHasherTests
{
    private static readonly byte[] Pepper = [.. Enumerable.Repeat((byte)5, 32)];

    [Fact]
    public void Hash_ACode_IsTheHmacSha256OfItUnderThePepperInLowerCaseHex()
    {
        // Arrange
        var hasher = new InvitationCodeHasher(Pepper);

        // Act
        var hash = hasher.Hash("ABCDEFGH");

        // Assert
        hash.Should().Be(Convert.ToHexStringLower(HMACSHA256.HashData(Pepper, Encoding.UTF8.GetBytes("ABCDEFGH"))));
    }

    [Fact]
    public void Hash_ACode_IsNotThePlainSha256()
    {
        // Arrange: eight characters are 40 bits, so an unkeyed hash in a stolen database would be brute-forced in seconds.
        var hasher = new InvitationCodeHasher(Pepper);

        // Act
        var hash = hasher.Hash("ABCDEFGH");

        // Assert
        hash.Should().NotBe(AuthSecrets.Hash("ABCDEFGH"));
    }

    [Fact]
    public void Hash_TwoPeppers_GiveTwoHashes()
    {
        // Act
        var first = new InvitationCodeHasher(Pepper).Hash("ABCDEFGH");
        var second = new InvitationCodeHasher([.. Enumerable.Repeat((byte)6, 32)]).Hash("ABCDEFGH");

        // Assert
        first.Should().NotBe(second);
    }

    [Fact]
    public void Constructor_AnEmptyPepper_Throws()
    {
        // Act
        var act = () => new InvitationCodeHasher([]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
