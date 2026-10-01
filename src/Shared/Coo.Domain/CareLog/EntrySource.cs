namespace Coo.Domain.CareLog;

/// <summary>How an entry was created. Immutable: an edit does not change it (Spec 001 §4.5).</summary>
public sealed record EntrySource
{
    private EntrySource(EntrySourceKind kind, string? promptVersion)
    {
        Kind = kind;
        PromptVersion = promptVersion;
    }

    public EntrySourceKind Kind { get; }

    /// <summary>The prompt version that produced an AI-parsed entry (ADR 0005 §1); null otherwise.</summary>
    public string? PromptVersion { get; }

    public static EntrySource Manual { get; } = new(EntrySourceKind.Manual, null);

    public static EntrySource Timer { get; } = new(EntrySourceKind.Timer, null);

    public static EntrySource Watch { get; } = new(EntrySourceKind.Watch, null);

    public static EntrySource AiParsed(string promptVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        return new(EntrySourceKind.AiParsed, promptVersion);
    }
}

public enum EntrySourceKind
{
    Manual,
    Timer,
    Watch,
    AiParsed,
}
