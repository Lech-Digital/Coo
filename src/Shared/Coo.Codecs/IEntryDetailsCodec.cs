using Coo.Domain.CareLog;

namespace Coo.Codecs;

/// <summary>
/// Turns one kind's details into JSON and back (Spec 003 §5). The only way details are read or written:
/// repositories and contract mapping go through it, never through JSON directly.
/// </summary>
public interface IEntryDetailsCodec
{
    EntryKindCode Kind { get; }

    int CurrentVersion { get; }

    /// <summary>
    /// Encodes through the write gate (Spec 003 §6). <paramref name="previous"/> is the stored document being
    /// replaced, if any; its unknown top-level properties are carried over (§7).
    /// </summary>
    /// <exception cref="DetailsEncodingException">The value would not read back as itself: a codec bug.</exception>
    string Encode(EntryDetails details, string? previous = null);

    DecodeOutcome Decode(string json);
}

/// <summary>What reading a stored or received details document produced (Spec 003 §5).</summary>
public abstract record DecodeOutcome
{
    private protected DecodeOutcome()
    {
    }
}

/// <summary>The document reads, upcasts and passes the Domain factories.</summary>
public sealed record Decoded(EntryDetails Details, int StoredVersion) : DecodeOutcome;

/// <summary>Valid, but newer than this client: kept as-is and shown read-only (ADR 0010 §3).</summary>
public sealed record Unrepresentable(string Reason) : DecodeOutcome;

/// <summary>Broken in every version: quarantined on the phone, rejected by the server (ADR 0010 §7, G4).</summary>
public sealed record Malformed(string Reason) : DecodeOutcome;

/// <summary>A details value did not survive its own round trip. Always a programming error (Spec 003 §6).</summary>
public sealed class DetailsEncodingException : Exception
{
    public DetailsEncodingException()
    {
    }

    public DetailsEncodingException(string message)
        : base(message)
    {
    }

    public DetailsEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reason codes for <see cref="Unrepresentable"/> and <see cref="Malformed"/>.</summary>
public static class DecodeReasons
{
    public const string NotJson = "details.not-json";
    public const string NotObject = "details.not-object";
    public const string MissingVersion = "details.missing:v";
    public const string VersionInvalid = "details.version-invalid";
    public const string VersionTooNew = "details.version-too-new";

    public static string Invalid(string? path) => $"details.invalid:{path ?? "$"}";

    public static string Missing(string property) => $"details.missing:{property}";

    public static string Unexpected(string property) => $"details.unexpected:{property}";

    public static string UnknownValue(string property) => $"details.unknown-value:{property}";
}
