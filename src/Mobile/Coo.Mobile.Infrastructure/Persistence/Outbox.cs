using Coo.Domain.Common.Versioning;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// Appends outbox rows (ADR 0002 §2). Always called inside the transaction of the write it records. The row
/// names the record and its version; sync sends the record's current state (Spec 004 §4.3).
/// </summary>
internal static class Outbox
{
    public static Task AppendAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entityType, Guid entityId, bool isDelete,
        HlcStamp version, TimeProvider time, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
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
