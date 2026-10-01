using System.Text.RegularExpressions;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog;

/// <summary>A kind's permanent code: never renamed, never reused (Spec 001 §4.8, ADR 0010 §1).</summary>
public readonly partial record struct EntryKindCode
{
    public const string Invalid = "kind.code-invalid";

    private EntryKindCode(string value) => Value = value;

    public string Value { get; }

    public static Result<EntryKindCode> From(string? value) =>
        value is not null && CodePattern().IsMatch(value)
            ? new EntryKindCode(value)
            : Result<EntryKindCode>.Failure(Invalid);

    /// <summary>For codes declared in source, where an invalid code is a programming error.</summary>
    internal static EntryKindCode Known(string value) =>
        From(value) is { IsSuccess: true } result ? result.Value : throw new ArgumentException($"Invalid kind code '{value}'.", nameof(value));

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
