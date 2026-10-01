using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>"Keep both" decisions in SQLite (Spec 007 §7). Each save appends its outbox row in the same transaction.</summary>
internal sealed class DuplicateDismissalStore(LocalDatabase database, TimeProvider time) : IDuplicateDismissalStore
{
    public async Task SaveAsync(DuplicateDismissal dismissal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dismissal);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted)
            VALUES ($id, $household, $a, $b, $version_wall_ms, $version_counter, $version_device, $deleted)
            ON CONFLICT (id) DO UPDATE SET version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                version_device = excluded.version_device, is_deleted = excluded.is_deleted;
            """,
            cancellationToken,
            transaction,
            [
                ("$id", Columns.Id(dismissal.Id.Value)),
                ("$household", Columns.Id(dismissal.HouseholdId.Value)),
                ("$a", Columns.Id(dismissal.Pair.First.Value)),
                ("$b", Columns.Id(dismissal.Pair.Second.Value)),
                .. Columns.Stamp(dismissal.Version),
                ("$deleted", dismissal.IsDeleted ? 1 : 0),
            ]);
        await Outbox.AppendAsync(connection, transaction, SyncedEntityType.DuplicateDismissal, dismissal.Id.Value, dismissal.IsDeleted, dismissal.Version, time, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<EntryPair>> ListAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.Command(
            "SELECT entry_a, entry_b FROM duplicate_dismissals WHERE household_id = $h AND is_deleted = 0;", null, ("$h", Columns.Id(household.Value)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pairs = new HashSet<EntryPair>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(0)) is { } a && Columns.ParseGuid(reader.GetString(1)) is { } b && a != b)
                pairs.Add(new EntryPair(new EntryId(a), new EntryId(b)));
        }

        return pairs;
    }
}
