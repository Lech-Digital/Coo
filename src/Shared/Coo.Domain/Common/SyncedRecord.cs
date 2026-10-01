using Coo.Domain.Common.Versioning;

namespace Coo.Domain.Common;

/// <summary>A record whose state converges across devices by last-writer-wins on <see cref="Version"/>.</summary>
public interface IVersioned
{
    HlcStamp Version { get; }
}

/// <summary>
/// The base of every synced record (Spec 001 §3.4): its own aggregate, a tombstone flag, and a version stamp
/// that strictly increases with every write.
/// </summary>
public abstract class SyncedRecord<TId> : IVersioned
    where TId : struct
{
    protected SyncedRecord(TId id, HouseholdId householdId, HlcStamp version, bool isDeleted)
    {
        Id = id;
        HouseholdId = householdId;
        Version = version;
        IsDeleted = isDeleted;
    }

    public TId Id { get; }

    public HouseholdId HouseholdId { get; }

    public HlcStamp Version { get; private set; }

    public bool IsDeleted { get; private set; }

    /// <summary>Writes a tombstone. A tombstone is a write like any other, and carries its own version.</summary>
    public Result Delete(HlcStamp version)
    {
        if (IsDeleted)
            return Result.Failure(RecordErrors.AlreadyDeleted);

        if (version <= Version)
            return Result.Failure(RecordErrors.StaleVersion);

        IsDeleted = true;
        Version = version;
        return Result.Success;
    }

    /// <summary>The first two steps of every mutation's pipeline (Spec 001 §6.4, step 1).</summary>
    protected Result EnsureWritable(HlcStamp version) =>
        IsDeleted ? Result.Failure(RecordErrors.Deleted)
        : version <= Version ? Result.Failure(RecordErrors.StaleVersion)
        : Result.Success;

    /// <summary>Records a successful write. Call only after every rule has passed.</summary>
    protected void Stamp(HlcStamp version) => Version = version;
}

public static class RecordErrors
{
    public const string Deleted = "record.deleted";
    public const string AlreadyDeleted = "record.already-deleted";
    public const string StaleVersion = "record.stale-version";
}

/// <summary>The one conflict rule, used by the device, the server and the watch alike (ADR 0002 §3).</summary>
public static class LastWriterWins
{
    public static T Pick<T>(T a, T b)
        where T : IVersioned
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Version.CompareTo(b.Version) >= 0 ? a : b;
    }
}
