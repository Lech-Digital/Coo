using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Coo.Domain.CareLog;

namespace Coo.Codecs;

/// <summary>One step of a kind's version chain: a pure function from version <see cref="From"/> to the next.</summary>
internal sealed record Upcaster(int From, Func<JsonObject, JsonObject> Apply);

/// <summary>
/// The shared machinery of every codec (Spec 003 §5–§8): version handling, upcasting, the write gate and the
/// merge of unknown properties. A kind's codec supplies only the mapping between its details and its DTO.
/// </summary>
internal abstract class EntryDetailsCodec<TDetails, TDto> : IEntryDetailsCodec
    where TDetails : EntryDetails
    where TDto : class
{
    private readonly Lazy<HashSet<string>> _knownProperties;

    protected EntryDetailsCodec() =>
        _knownProperties = new(() => [.. TypeInfo.Properties.Select(p => p.Name)]);

    public abstract EntryKindCode Kind { get; }

    public abstract int CurrentVersion { get; }

    /// <summary>The upcasters from version 1 up to <see cref="CurrentVersion"/>, in order.</summary>
    protected virtual IReadOnlyList<Upcaster> Upcasters => [];

    protected abstract JsonTypeInfo<TDto> TypeInfo { get; }

    protected abstract TDto ToDto(TDetails details);

    /// <summary>Maps a current-version DTO to details through the Domain factories.</summary>
    protected abstract DecodeOutcome FromDto(TDto dto);

    public string Encode(EntryDetails details, string? previous = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details is not TDetails typed)
            throw new ArgumentException($"Codec '{Kind}' encodes {typeof(TDetails).Name}, not {details.GetType().Name}.", nameof(details));

        var canonical = Serialize(typed);
        var document = previous is null ? canonical : MergeUnknownProperties(canonical, previous);

        // The write gate (Spec 003 §6): decode what we are about to write, and require the same value back,
        // which then encodes to the same bytes. Value equality alone would miss a lost offset.
        if (Decode(document) is not Decoded { Details: TDetails back })
            throw new DetailsEncodingException($"Codec '{Kind}' produced a document it cannot decode: {document}");

        if (!back.Equals(typed))
            throw new DetailsEncodingException($"Codec '{Kind}' does not round-trip {typed}: it reads back as {back}.");

        if (Serialize(back) != canonical)
            throw new DetailsEncodingException($"Codec '{Kind}' is not stable: {canonical} re-encodes as {Serialize(back)}.");

        return document;
    }

    public DecodeOutcome Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return new Malformed(DecodeReasons.NotJson);
        }

        if (node is not JsonObject document)
            return new Malformed(DecodeReasons.NotObject);

        if (!document.TryGetPropertyValue("v", out var versionNode)
            || versionNode is not JsonValue versionValue
            || versionValue.GetValueKind() != JsonValueKind.Number
            || !versionValue.TryGetValue<int>(out var version))
            return new Malformed(DecodeReasons.MissingVersion);

        if (version < 1)
            return new Malformed(DecodeReasons.VersionInvalid);

        if (version > CurrentVersion)
            return new Unrepresentable(DecodeReasons.VersionTooNew);

        document = Upcast(document, version);

        TDto? dto;
        try
        {
            dto = document.Deserialize(TypeInfo);
        }
        catch (JsonException e)
        {
            return new Malformed(DecodeReasons.Invalid(e.Path));
        }

        if (dto is null)
            return new Malformed(DecodeReasons.NotObject);

        var outcome = FromDto(dto);
        return outcome is Decoded decoded ? decoded with { StoredVersion = version } : outcome;
    }

    private JsonObject Upcast(JsonObject document, int version)
    {
        for (var step = version; step < CurrentVersion; step++)
        {
            var upcaster = Upcasters.SingleOrDefault(u => u.From == step)
                ?? throw new InvalidOperationException($"Codec '{Kind}' has no upcaster from version {step}.");
            document = upcaster.Apply(document);
            document["v"] = step + 1;
        }

        return document;
    }

    protected static Decoded Ok(EntryDetails details) => new(details, StoredVersion: 0);

    /// <summary>Volumes are written without trailing zeros, so equal values always encode to the same bytes.</summary>
    protected static decimal Canonical(decimal millilitres) =>
        decimal.Parse(millilitres.ToString("0.#", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private string Serialize(TDetails details) => JsonSerializer.Serialize(ToDto(details), TypeInfo);

    /// <summary>
    /// Carries over the top-level properties of <paramref name="previous"/> that this codec does not know
    /// (Spec 003 §7). A known property is never copied back: if it is absent now, the person cleared it.
    /// </summary>
    private string MergeUnknownProperties(string canonical, string previous)
    {
        if (JsonNode.Parse(previous) is not JsonObject old
            || old["v"] is not JsonValue v || !v.TryGetValue<int>(out var oldVersion) || oldVersion < 1 || oldVersion > CurrentVersion)
            throw new ArgumentException("The previous document is not a decodable details document.", nameof(previous));

        // Upcast first: an old version's renamed properties are not "unknown", they are old.
        old = Upcast(old, oldVersion);

        var merged = JsonNode.Parse(canonical)!.AsObject();
        foreach (var (name, value) in old)
        {
            if (!_knownProperties.Value.Contains(name) && !merged.ContainsKey(name))
                merged[name] = value?.DeepClone();
        }

        return merged.ToJsonString(CodecJson.Options);
    }
}

/// <summary>
/// A closed map between a C# enum and its JSON strings. Explicit, so a renamed C# member never changes
/// stored data, and an unknown string is detected rather than guessed.
/// </summary>
internal sealed class StringEnum<T>(params (string Code, T Value)[] pairs)
    where T : struct, Enum
{
    private readonly Dictionary<string, T> _byCode = pairs.ToDictionary(p => p.Code, p => p.Value, StringComparer.Ordinal);
    private readonly Dictionary<T, string> _byValue = pairs.ToDictionary(p => p.Value, p => p.Code);

    public bool TryParse(string? code, out T value)
    {
        if (code is not null && _byCode.TryGetValue(code, out value))
            return true;

        value = default;
        return false;
    }

    public string Code(T value) =>
        _byValue.TryGetValue(value, out var code) ? code : throw new ArgumentOutOfRangeException(nameof(value), value, "No JSON code for this value.");
}
