using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Domain.Tests.Households;

/// <summary>Spec 011b §3 and §10.6: the code (alphabet, normalisation, length) and the invitation (72 h, used once, withdrawn).</summary>
public sealed class InvitationTests
{
    private static readonly InvitationId Id = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"));

    [Fact]
    public void Alphabet_Always_HasThirtyOneUnambiguousSymbols()
    {
        // Act
        var alphabet = InvitationCode.Alphabet;

        // Assert
        alphabet.Should().HaveLength(31);
        alphabet.Distinct().Should().HaveCount(31);
        alphabet.Should().NotContainAny("0", "O", "1", "I", "L");
    }

    [Fact]
    public void Parse_LowerCaseWithSpacesAndHyphen_Normalises()
    {
        // Arrange
        const string typed = " abcd-ef gh ";

        // Act
        var code = InvitationCode.Parse(typed);

        // Assert
        code.Value.Value.Should().Be("ABCDEFGH");
        code.Value.Display.Should().Be("ABCD-EFGH");
        code.Value.ToString().Should().Be("ABCD-EFGH");
    }

    [Theory]
    [InlineData("ABCD\u00ADEFGH", "a soft hyphen, as a messenger may insert")]
    [InlineData("ABCD\u2010EFGH", "U+2010 hyphen")]
    [InlineData("ABCD\u2011EFGH", "U+2011 non-breaking hyphen")]
    [InlineData("ABCD\u2013EFGH", "U+2013 en dash")]
    [InlineData("ABCD\u2014EFGH", "U+2014 em dash")]
    [InlineData("ABCD\u200BEFGH", "a zero-width space")]
    [InlineData("\uFEFFABCDEFGH", "a byte-order mark")]
    [InlineData("ABCD\u00A0EFGH", "a no-break space")]
    public void Parse_UnicodeDashesAndInvisibleFormatCharacters_AreIgnored(string text, string why)
    {
        // Act
        var code = InvitationCode.Parse(text);

        // Assert
        code.IsSuccess.Should().BeTrue(why);
        code.Value.Value.Should().Be("ABCDEFGH");
    }

    [Theory]
    [InlineData("ABCD-EFG0")]
    [InlineData("ABCD-EFGO")]
    [InlineData("ABCD-EFG1")]
    [InlineData("ABCD-EFGI")]
    [InlineData("ABCD-EFGL")]
    [InlineData("abcd-efgl")]
    public void Parse_AnAmbiguousCharacter_IsRefused(string text)
    {
        // Act
        var code = InvitationCode.Parse(text);

        // Assert
        code.Error!.Code.Should().Be(Invitation.InvalidCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFG")]
    [InlineData("ABCD-EFGHJ")]
    [InlineData("ABCD_EFGH")]
    [InlineData("ÄBCD-EFGH")]
    public void Parse_WrongLengthOrCharacter_IsRefused(string? text)
    {
        // Act
        var code = InvitationCode.Parse(text);

        // Assert
        code.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void FromRandom_IndicesGiven_UsesTheAlphabetInOrder()
    {
        // Arrange: the randomness is passed in (CS-HEX-01: the Domain has no RNG of its own).
        var next = 0;
        int Index(int max)
        {
            max.Should().Be(31);
            return next++;
        }

        // Act
        var code = InvitationCode.FromRandom(Index);

        // Assert
        code.Value.Should().Be("ABCDEFGH");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void FromRandom_IndexOutOfRange_Throws(int index)
    {
        // Act
        var act = () => InvitationCode.FromRandom(_ => index);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsUsable_AtExactlySeventyTwoHours_IsFalse()
    {
        // Arrange
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);

        // Act
        var justBefore = invitation.IsUsable(A.Now.AddHours(72).AddTicks(-1));
        var atExpiry = invitation.IsUsable(A.Now.AddHours(72));

        // Assert
        invitation.ExpiresAt.Should().Be(A.Now.AddHours(72));
        justBefore.Should().BeTrue();
        atExpiry.Should().BeFalse();
    }

    [Fact]
    public void Accept_Twice_IsRefusedTheSecondTime()
    {
        // Arrange
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);

        // Act
        var first = invitation.Accept(A.OtherParent, A.Now.AddHours(1));
        var second = invitation.Accept(A.OtherParent, A.Now.AddHours(2));

        // Assert
        first.IsSuccess.Should().BeTrue();
        second.Error!.Code.Should().Be(Invitation.InvalidCode);
        invitation.AcceptedAt.Should().Be(A.Now.AddHours(1));
        invitation.WasAcceptedBy(A.OtherParent).Should().BeTrue();
        invitation.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Accept_AfterWithdraw_IsRefused()
    {
        // Arrange
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);
        invitation.Withdraw(A.Now.AddMinutes(5));

        // Act
        var result = invitation.Accept(A.OtherParent, A.Now.AddMinutes(6));

        // Assert
        result.IsFailure.Should().BeTrue();
        invitation.RevokedAt.Should().Be(A.Now.AddMinutes(5));
    }

    [Fact]
    public void Withdraw_AcceptedInvitation_ChangesNothing()
    {
        // Arrange: an accepted invitation stays as it was, so a retried accept stays idempotent (spec §10.3).
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);
        invitation.Accept(A.OtherParent, A.Now.AddHours(1));

        // Act
        invitation.Withdraw(A.Now.AddHours(2));

        // Assert
        invitation.RevokedAt.Should().BeNull();
    }

    [Fact]
    public void Create_ForTheOwnerRole_Throws()
    {
        // Act
        var act = () => Invitation.Create(Id, A.Household, Role.Owner, A.Parent, A.Now);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>("ownership comes only from creating a family (ruling 14)");
    }
}
