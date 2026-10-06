using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>
/// An invitation's code (Spec 011b §3, ADR 0003 §6): 8 characters from an alphabet without 0/O and 1/I/L. Entered in any
/// case; whitespace and hyphens are ignored. The Domain never draws randomness itself: <see cref="FromRandom"/> is given it.
/// </summary>
public sealed record InvitationCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 8;

    private InvitationCode(string value) => Value = value;

    /// <summary>The 8 characters, capitals, no hyphen: what the link carries and the server hashes.</summary>
    public string Value { get; }

    /// <summary>"ABCD-EFGH": how people read and type it.</summary>
    public string Display => $"{Value[..4]}-{Value[4..]}";

    public static Result<InvitationCode> Parse(string? text)
    {
        if (text is null)
            return Result<InvitationCode>.Failure(Invitation.InvalidCode);
        var chars = text.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray();
        return chars.Length == Length && chars.All(c => Alphabet.Contains(c, StringComparison.Ordinal))
            ? new InvitationCode(new string(chars))
            : Result<InvitationCode>.Failure(Invitation.InvalidCode);
    }

    /// <param name="nextIndex">Given the alphabet's size, a uniformly random index below it (the server passes
    /// <c>RandomNumberGenerator.GetInt32</c>).</param>
    public static InvitationCode FromRandom(Func<int, int> nextIndex)
    {
        ArgumentNullException.ThrowIfNull(nextIndex);
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            var index = nextIndex(Alphabet.Length);
            if (index < 0 || index >= Alphabet.Length)
                throw new ArgumentOutOfRangeException(nameof(nextIndex), index, "The index must be below the alphabet's size.");
            chars[i] = Alphabet[index];
        }

        return new InvitationCode(new string(chars));
    }

    public override string ToString() => Display;
}
