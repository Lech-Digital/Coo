using Coo.Domain.Common;

namespace Coo.Domain.CareLog;

/// <summary>Optional free text on an entry: trimmed, 1–500 characters. Never sent to the AI (Spec 001 §4.7).</summary>
public sealed record Note
{
    public const int MaxLength = 500;
    public const string TooLong = "note.too-long";

    private Note(string text) => Text = text;

    public string Text { get; }

    /// <summary>Whitespace-only or null text means "no note", which is a success with a null value.</summary>
    public static Result<Note?> From(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Result<Note?>.Ok(null);

        return trimmed.Length > MaxLength
            ? DomainError.Of(TooLong, "max", MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new Note(trimmed);
    }

    public override string ToString() => Text;
}
