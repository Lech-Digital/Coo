using Coo.Contracts.Sync;

namespace Coo.Contracts.Wearables;

// The watch's messages (ADR 0009 §6). Golden fixtures in wearables/contract-fixtures are the source of truth for both the
// phone (these records) and the Kotlin watch app. Versioned by adding, never by renaming; the phone accepts the current
// and the previous schema version.

public static class WatchSchema
{
    public const int Current = 1;

    /// <summary>The oldest a watch may send: a watch and a phone update independently.</summary>
    public const int Oldest = 1;

    /// <summary>The Data Layer path of an entry the watch logged: <c>/entries/{entryId}</c> (one item per entry, latest state).</summary>
    public const string EntriesPath = "/entries/";

    /// <summary>The Data Layer path of the phone's snapshot.</summary>
    public const string SnapshotPath = "/snapshot";
}

/// <summary>
/// An entry's latest state from the watch (ADR 0009 §2): created there, or a phone timer stopped there, or deleted. The ID
/// is a UUIDv7 made on the watch and the version an HLC stamp with the watch's own device ID; the phone keeps both.
/// </summary>
/// <param name="Kind"><c>breast</c>, <c>bottle</c>, <c>diaper</c> or <c>sleep</c>.</param>
/// <param name="StartMs">The tap, Unix milliseconds; a session's start.</param>
/// <param name="EndMs">A session's end; null while it runs.</param>
/// <param name="Side"><c>left</c> or <c>right</c>, for <c>breast</c>.</param>
/// <param name="Contents"><c>wet</c>, <c>dirty</c> or <c>mixed</c>, for <c>diaper</c>.</param>
/// <param name="Milk"><c>breast</c> or <c>formula</c>, for <c>bottle</c>.</param>
public sealed record WatchEntryStateV1(
    int SchemaVersion,
    Guid EntryId,
    Guid BabyId,
    string Kind,
    bool Deleted,
    long StartMs,
    long? EndMs,
    HlcStampDto Version,
    string? Side = null,
    string? Contents = null,
    decimal? BottleMl = null,
    string? Milk = null);

/// <summary>What the watch shows (ADR 0009 §3), republished by the phone when it changes.</summary>
/// <param name="VolumeUnit"><c>ml</c> or <c>oz</c>.</param>
/// <param name="Language">Two letters, the phone app's language.</param>
public sealed record WatchSnapshotV1(
    int SchemaVersion,
    IReadOnlyList<WatchBabyV1> Babies,
    Guid? SelectedBabyId,
    IReadOnlyList<WatchRunningV1> Running,
    long? LastFeedMs,
    string? LastSide,
    long? LastDiaperMs,
    long? AwakeSinceMs,
    WatchNapWindowV1? NextNap,
    decimal? LastBottleMl,
    string VolumeUnit,
    string Language,
    long PublishedMs);

public sealed record WatchBabyV1(Guid Id, string Name);

/// <param name="Kind"><c>breast</c>, <c>sleep</c>, <c>pump</c> or <c>tummy</c>.</param>
public sealed record WatchRunningV1(Guid EntryId, Guid? BabyId, string Kind, long StartMs, string? Side = null);

public sealed record WatchNapWindowV1(long EarliestMs, long LatestMs, bool IsBedtime);
