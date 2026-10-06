using Coo.Codecs;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// The care log in SQLite (Spec 004 §5, §6). Writes: previous details, codec write gate, row and outbox, all in one
/// transaction. Reads: through the read guard, so one bad row never blanks the timeline.
/// </summary>
internal sealed class CareLogStore(LocalDatabase database, TimeProvider time, LocalWrites writes) : ICareLogStore
{

    private const string EntityType = SyncedEntityType.CareEntry;

    public async Task SaveAsync(CareEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SaveInAsync(connection, transaction, entry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        writes.Notify();
    }

    public async Task<StoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await ReadRowsAsync(
            connection, $"SELECT {CareEntryRow.SelectColumns} FROM care_entries WHERE id = $id;", cancellationToken, ("$id", Columns.Id(id.Value)));
        return rows.Count == 0 ? null : await ClassifyAndRecordAsync(connection, rows[0], cancellationToken);
    }

    public async Task<IReadOnlyList<StoredEntry>> ListAsync(
        HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await ReadRowsAsync(
            connection,
            $"""
            SELECT {CareEntryRow.SelectColumns} FROM care_entries
            WHERE household_id = $household AND is_deleted = 0 AND start_utc_ms >= $from AND start_utc_ms < $to
            ORDER BY start_utc_ms DESC;
            """,
            cancellationToken,
            ("$household", Columns.Id(household.Value)),
            ("$from", windowStart.ToUnixTimeMilliseconds()),
            ("$to", windowEnd.ToUnixTimeMilliseconds()));

        var entries = new List<StoredEntry>(rows.Count);
        foreach (var row in rows)
            entries.Add(await ClassifyAndRecordAsync(connection, row, cancellationToken));
        return entries;
    }

    public async Task<bool> DeleteAsync(EntryId id, HlcStamp version, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await SetDeletedInAsync(connection, transaction, new EntryVersion(id, version), deleted: true, cancellationToken))
            return false;

        await transaction.CommitAsync(cancellationToken);
        writes.Notify();
        return true;
    }

    public async Task<bool> WriteAllAsync(CareLogWrites changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entry in changes.Saves)
            await SaveInAsync(connection, transaction, entry, cancellationToken);
        foreach (var delete in changes.Deletes)
        {
            if (!await SetDeletedInAsync(connection, transaction, delete, deleted: true, cancellationToken))
                return false;
        }

        foreach (var restore in changes.Restores)
        {
            if (!await SetDeletedInAsync(connection, transaction, restore, deleted: false, cancellationToken))
                return false;
        }

        await transaction.CommitAsync(cancellationToken);
        writes.Notify();
        return true;
    }

    public async Task<StoreCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var h = ("$household", (object?)Columns.Id(household.Value));
        var outbox = await connection.ScalarAsync("SELECT count(*) FROM outbox;", cancellationToken);
        var readOnly = await connection.ScalarAsync(
            "SELECT count(*) FROM care_entries WHERE household_id = $household AND is_deleted = 0 AND integrity = 'read-only';", cancellationToken, null, h);
        var quarantined = await connection.ScalarAsync(
            "SELECT count(*) FROM care_entries WHERE household_id = $household AND is_deleted = 0 AND integrity = 'quarantined';", cancellationToken, null, h);
        return new StoreCounts(ToInt(outbox), ToInt(readOnly), ToInt(quarantined));

        static int ToInt(object? value) => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Previous details, codec write gate, row and outbox, inside the caller's transaction.</summary>
    private async Task SaveInAsync(SqliteConnection connection, SqliteTransaction transaction, CareEntry entry, CancellationToken cancellationToken)
    {
        // 1. The stored document, so the codec carries over properties this build does not know (Spec 003 §7).
        var previous = (string?)await connection.ScalarAsync(
            "SELECT details FROM care_entries WHERE id = $id;", cancellationToken, transaction, ("$id", Columns.Id(entry.Id.Value)));

        // 2. The write gate. A DetailsEncodingException leaves the transaction uncommitted: nothing is written.
        var details = EntryDetailsCodecs.For(entry.Details).Encode(entry.Details, previous);

        // 3. The row, and 4. its outbox entry.
        await connection.ExecuteAsync(UpsertSql, cancellationToken, transaction, Parameters(entry, details));
        await Outbox.AppendAsync(connection, transaction, EntityType, entry.Id.Value, entry.IsDeleted, entry.Version, time, cancellationToken);
    }

    /// <summary>
    /// Writes a tombstone, or takes one back, with its outbox row, inside the caller's transaction. The details are kept
    /// as they are: neither needs decoding, so a tombstone of a read-only or quarantined row works too.
    /// </summary>
    /// <returns>False when there is no such entry, or it already is in that state.</returns>
    private async Task<bool> SetDeletedInAsync(
        SqliteConnection connection, SqliteTransaction transaction, EntryVersion change, bool deleted, CancellationToken cancellationToken)
    {
        var updated = await connection.ExecuteAsync(
            """
            UPDATE care_entries
            SET is_deleted = $deleted, version_wall_ms = $version_wall_ms, version_counter = $version_counter, version_device = $version_device
            WHERE id = $id AND is_deleted = $was;
            """,
            cancellationToken,
            transaction,
            [("$id", Columns.Id(change.Id.Value)), ("$deleted", deleted ? 1 : 0), ("$was", deleted ? 0 : 1), .. Columns.Stamp(change.Version)]);
        if (updated == 0)
            return false;

        // A restore sends the record's current state, so it goes out as an upsert (Spec 004 §4.3).
        await Outbox.AppendAsync(connection, transaction, EntityType, change.Id.Value, isDelete: deleted, change.Version, time, cancellationToken);
        return true;
    }

    /// <summary>Classifies a row, and records its integrity when it changed (Spec 004 §6, step 3).</summary>
    internal static async Task<StoredEntry> ClassifyAndRecordAsync(SqliteConnection connection, CareEntryRow row, CancellationToken cancellationToken)
    {
        var classified = CareEntryClassifier.Classify(row);
        if (classified.Integrity != row.Integrity)
        {
            await connection.ExecuteAsync(
                "UPDATE care_entries SET integrity = $integrity, integrity_reason = $reason WHERE id = $id;",
                cancellationToken,
                null,
                ("$integrity", classified.Integrity), ("$reason", classified.Reason), ("$id", row.Id));
        }

        return classified.Entry;
    }

    internal static async Task<List<CareEntryRow>> ReadRowsAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.Command(sql, null, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CareEntryRow>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(CareEntryRow.Read(reader));
        return rows;
    }

    private const string UpsertSql =
        """
        INSERT INTO care_entries (
            id, household_id, kind, subject_baby_id, subject_person, is_session, start_utc_ms, start_offset_min,
            end_utc_ms, end_offset_min, logged_by, source, source_prompt_version, note, details,
            version_wall_ms, version_counter, version_device, is_deleted, integrity, integrity_reason)
        VALUES (
            $id, $household_id, $kind, $subject_baby_id, $subject_person, $is_session, $start_utc_ms, $start_offset_min,
            $end_utc_ms, $end_offset_min, $logged_by, $source, $source_prompt_version, $note, $details,
            $version_wall_ms, $version_counter, $version_device, $is_deleted, 'ok', NULL)
        ON CONFLICT (id) DO UPDATE SET
            is_session = excluded.is_session, start_utc_ms = excluded.start_utc_ms, start_offset_min = excluded.start_offset_min,
            end_utc_ms = excluded.end_utc_ms, end_offset_min = excluded.end_offset_min, note = excluded.note,
            details = excluded.details, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
            version_device = excluded.version_device, is_deleted = excluded.is_deleted, integrity = 'ok', integrity_reason = NULL;
        """;

    private static (string Name, object? Value)[] Parameters(CareEntry entry, string details)
    {
        var (start, end) = entry.Timing switch
        {
            PointTiming point => (point.At, (DateTimeOffset?)null),
            SessionTiming session => (session.Range.Start, session.Range.End),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Timing.GetType().Name, "Unknown timing."),
        };
        var (startMs, startOffset) = Columns.Instant(start);
        var endParts = end is { } e ? Columns.Instant(e) : ((long, int)?)null;

        return
        [
            ("$id", Columns.Id(entry.Id.Value)),
            ("$household_id", Columns.Id(entry.HouseholdId.Value)),
            ("$kind", entry.KindCode.Value),
            ("$subject_baby_id", entry.Subject is BabySubject baby ? Columns.Id(baby.BabyId.Value) : null),
            ("$subject_person", entry.Subject is CaregiverSubject caregiver ? Columns.Person(caregiver.Person) : null),
            ("$is_session", entry.Timing is SessionTiming ? 1 : 0),
            ("$start_utc_ms", startMs),
            ("$start_offset_min", startOffset),
            ("$end_utc_ms", endParts?.Item1),
            ("$end_offset_min", endParts?.Item2),
            ("$logged_by", Columns.Person(entry.LoggedBy)),
            ("$source", Columns.Source(entry.Source)),
            ("$source_prompt_version", entry.Source.PromptVersion),
            ("$note", entry.Note?.Text),
            ("$details", details),
            .. Columns.Stamp(entry.Version),
            ("$is_deleted", entry.IsDeleted ? 1 : 0),
        ];
    }
}
