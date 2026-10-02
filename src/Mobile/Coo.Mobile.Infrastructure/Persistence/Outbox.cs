using Coo.Contracts.Sync;
using Coo.Domain.Common.Versioning;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// The synced entity types (ADR 0010 §1, Spec 007 §7), taken from the wire contract's one list
/// (<see cref="SyncEntityTypes"/>). Migration 0003 removed the database CHECK that repeated it, so a new type is one
/// line in the contract, not an outbox table rebuild.
/// </summary>
internal static class SyncedEntityType
{
    public const string CareEntry = SyncEntityTypes.CareEntry;
    public const string BabyProfile = SyncEntityTypes.BabyProfile;
    public const string HouseholdSettings = SyncEntityTypes.HouseholdSettings;
    public const string DuplicateDismissal = SyncEntityTypes.DuplicateDismissal;

    public static IReadOnlySet<string> All => SyncEntityTypes.All;
}

/// <summary>
/// Appends outbox rows (ADR 0002 §2). Always called inside the transaction of the write it records. The row
/// names the record and its version; sync sends the record's current state (Spec 004 §4.3).
/// </summary>
internal static class Outbox
{
    public static Task AppendAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entityType, Guid entityId, bool isDelete,
        HlcStamp version, TimeProvider time, CancellationToken cancellationToken) =>
        !SyncedEntityType.All.Contains(entityType)
            ? throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Not a synced entity type (SyncedEntityType.All).")
            : connection.ExecuteAsync(
            """
            INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
            VALUES ($change_id, $entity_type, $entity_id, $operation, $version_wall_ms, $version_counter, $version_device);
            """,
            cancellationToken,
            transaction,
            [
                ("$change_id", Columns.Id(Guid.CreateVersion7(time.GetUtcNow()))),
                ("$entity_type", entityType),
                ("$entity_id", Columns.Id(entityId)),
                ("$operation", isDelete ? "delete" : "upsert"),
                .. Columns.Stamp(version),
            ]);
}
