using System.Data;
using Coo.Api.Application.Sync;
using Coo.Codecs;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// Sync in PostgreSQL (Spec 011 §7.2, ADR 0002 §5–§6). An apply is one transaction: the stored version
/// (<c>FOR UPDATE</c>), <see cref="LastWriterWinsRule"/>, the household's next <c>seq</c> under its counter row lock, the
/// row, the result in <c>applied_changes</c>, and a <c>sync_conflicts</c> row when the write lost. Every row write
/// carries the same rule in SQL (household and version as a tuple), so a new ID two writers create at once still ends
/// with the higher version, and the affected-row count says which won. A pull reads the four tables in one snapshot.
/// Table and key names come from a closed map, never from input.
/// </summary>
internal sealed class SyncStore(NpgsqlDataSource dataSource) : ISyncStore
{
    private sealed record Table(string Name, string Key);

    private static readonly Dictionary<string, Table> Tables = new(StringComparer.Ordinal)
    {
        [SyncEntityTypes.CareEntry] = new("care_entries", "id"),
        [SyncEntityTypes.BabyProfile] = new("baby_profiles", "id"),
        [SyncEntityTypes.HouseholdSettings] = new("household_settings", "household_id"),
        [SyncEntityTypes.DuplicateDismissal] = new("duplicate_dismissals", "id"),
    };

    public async Task<ChangeResult?> FindResultAsync(Guid changeId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT result, reason FROM applied_changes WHERE change_id = @id;");
        command.Parameters.AddWithValue("id", changeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ToResult(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    public async Task<ChangeResult> ApplyAsync(HouseholdId household, Guid changeId, SyncWrite write, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        EnsureOwnHousehold(household, write);
        var table = Tables[write.EntityType];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. The household's seq counter row, locked until this transaction ends: one counter for all four tables
        // (ADR 0002 §6), and the lock serialises every write within one household. Always taken before the row lock of
        // step 2, so two writers never wait on each other's locks in opposite order (no 40P01). The counter only moves
        // for a write that lands (step 4), so seqs stay gapless.
        var seq = await LockNextSeqAsync(connection, transaction, household, cancellationToken);

        // 2. The stored version, locked until this transaction ends. A missing row locks nothing: a concurrent writer of
        // the same new ID is caught by the version guard in step 4 instead (PF1).
        var stored = await ReadStoredAsync(connection, transaction, table, write.EntityId, cancellationToken);

        // 3. The rule, with Coo.Domain's HlcStamp.
        var result = await DecideAsync(connection, transaction, household, changeId, write, stored, now, cancellationToken);

        // A delete of a record the server never stored needs nothing: only its own phone ever had it (ruling 1).
        if (result is null && write is TombstoneWrite && stored is null)
            result = ChangeResult.Applied;

        if (result is null)
        {
            // 4. The row, guarded by household and version in the same statement. One row means it won; none means a
            // concurrent writer stored this ID after step 2 (another household's, or a version at least as high), or a
            // care entry's kind differs from the stored one (the kind never changes).
            if (await WriteAsync(connection, transaction, household, write, table, seq, cancellationToken))
            {
                await AdvanceSeqAsync(connection, transaction, household, cancellationToken);
                result = ChangeResult.Applied;
            }
            else
            {
                stored = await ReadStoredAsync(connection, transaction, table, write.EntityId, cancellationToken);
                result = await DecideAsync(connection, transaction, household, changeId, write, stored, now, cancellationToken)
                    ?? (write is CareEntryWrite
                        ? ChangeResult.Rejected(RejectionReasons.ChangeMalformed)
                        : throw new InvalidOperationException($"{write.EntityType} {write.EntityId} was neither written nor outranked."));
            }
        }

        // 5. The result, once per change ID. If a concurrent attempt at the same change committed first, this one rolls
        // back whole (its seq included) and reports that attempt's result.
        if (!await TryRecordAsync(connection, transaction, changeId, result, now, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FindResultAsync(changeId, cancellationToken)
                ?? throw new InvalidOperationException($"Change {changeId} lost the race but has no stored result.");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ChangeResult> RecordRejectionAsync(Guid changeId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rejected = ChangeResult.Rejected(reason);
        return await TryRecordAsync(connection, null, changeId, rejected, now, cancellationToken)
            ? rejected
            : await FindResultAsync(changeId, cancellationToken) ?? rejected;
    }

    public async Task<PullPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // One snapshot for the head and the four tables. Commit order is seq order (the counter row lock), so a reader
        // never sees seq n+1 without seq n.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var head = bootstrapHead;
        if (cursor == 0 && head is null)
        {
            await using var read = new NpgsqlCommand("SELECT next_seq - 1 FROM households WHERE id = @h;", connection, transaction);
            read.Parameters.AddWithValue("h", household.Value);
            head = await read.ExecuteScalarAsync(cancellationToken) as long? ?? 0;
        }

        var rows = new List<PulledRecord>();
        rows.AddRange(await ReadCareEntriesAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        rows.AddRange(await ReadBabiesAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        rows.AddRange(await ReadSettingsAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        rows.AddRange(await ReadDismissalsAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        // Each table gave at most limit + 1 rows above the cursor, so the first limit + 1 of the union are exact.
        var ordered = rows.OrderBy(r => r.Seq).ToList();
        var page = ordered.Take(limit).ToList();
        var next = page.Count == 0 ? cursor : page[^1].Seq;
        var visible = page.Where(r => !(r.IsDeleted && head is { } h && r.Seq <= h)).ToList();
        return new PullPage(visible, next, ordered.Count > limit, head);
    }

    public async Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var sql in new[]
        {
            "DELETE FROM applied_changes WHERE applied_at < @before;",
            "DELETE FROM sync_conflicts WHERE recorded_at < @before;",
        })
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("before", before.UtcDateTime);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return deleted;
    }

    private static async Task<StoredVersion?> ReadStoredAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT household_id, version_wall_ms, version_counter, version_device FROM {table.Name} WHERE {table.Key} = @id FOR UPDATE;",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new StoredVersion(new HouseholdId(reader.GetGuid(0)), new HlcStamp(reader.GetInt64(1), reader.GetInt32(2), new DeviceId(reader.GetGuid(3))));
    }

    /// <summary>A rejection or a supersession (recorded as a conflict), or null when the write should be stored.</summary>
    private static async Task<ChangeResult?> DecideAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, Guid changeId, SyncWrite write, StoredVersion? stored,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (LastWriterWinsRule.Decide(household, stored, write.Version))
        {
            case WriteDecision.HouseholdMismatch:
                return ChangeResult.Rejected(RejectionReasons.HouseholdMismatch);
            case WriteDecision.Superseded:
                await RecordConflictAsync(connection, transaction, household, changeId, write, stored!.Version, now, cancellationToken);
                return ChangeResult.Superseded;
            default:
                return null;
        }
    }

    /// <summary>The write was built for the caller's household; one for another is a programming error, not a data problem.</summary>
    private static void EnsureOwnHousehold(HouseholdId household, SyncWrite write)
    {
        var owner = write switch
        {
            CareEntryWrite care => care.Entry.HouseholdId,
            BabyProfileWrite baby => baby.Baby.HouseholdId,
            DismissalWrite dismissal => dismissal.Dismissal.HouseholdId,
            HouseholdSettingsWrite settings => settings.Settings.Id,
            _ => household,
        };
        if (owner != household)
            throw new ArgumentException($"{write.EntityType} {write.EntityId} belongs to household {owner.Value}, not {household.Value}.", nameof(write));
    }

    private static async Task<long> LockNextSeqAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, CancellationToken cancellationToken)
    {
        await using var next = new NpgsqlCommand("SELECT next_seq FROM households WHERE id = @household FOR UPDATE;", connection, transaction);
        next.Parameters.AddWithValue("household", household.Value);
        return await next.ExecuteScalarAsync(cancellationToken) is long value
            ? value
            : throw new Coo.Api.Application.CareLog.HouseholdNotFoundException($"Household {household.Value} does not exist on the server.");
    }

    /// <summary>Only a write that landed moves the counter, so seqs stay gapless (the row lock is already held).</summary>
    private static async Task AdvanceSeqAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, CancellationToken cancellationToken)
    {
        await using var advance = new NpgsqlCommand("UPDATE households SET next_seq = next_seq + 1 WHERE id = @household;", connection, transaction);
        advance.Parameters.AddWithValue("household", household.Value);
        await advance.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>True when the row was written; false when the stored row is another household's or outranks this one.</summary>
    private static async Task<bool> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, SyncWrite write, Table table, long seq,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        switch (write)
        {
            case CareEntryWrite care:
                command.CommandText = CareEntryWriter.UpsertSql;
                CareEntryWriter.AddParameters(command, care.Entry, EntryDetailsCodecs.For(care.Entry.Details).Encode(care.Entry.Details, care.ReceivedDetails), seq);
                break;
            case BabyProfileWrite baby:
                command.CommandText =
                    """
                    INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
                    VALUES (@id, @household, @name, @nicknames, @birth, @due, @wall, @counter, @device, false, @seq)
                    ON CONFLICT (id) DO UPDATE SET name = excluded.name, nicknames = excluded.nicknames, birth_date = excluded.birth_date,
                        due_date = excluded.due_date, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = false, seq = excluded.seq
                    WHERE baby_profiles.household_id = excluded.household_id
                        AND (baby_profiles.version_wall_ms, baby_profiles.version_counter, baby_profiles.version_device)
                            < (excluded.version_wall_ms, excluded.version_counter, excluded.version_device);
                    """;
                command.Parameters.AddWithValue("id", baby.Baby.Id.Value);
                command.Parameters.AddWithValue("name", baby.Baby.Name);
                command.Parameters.AddWithValue("nicknames", baby.Baby.Nicknames.ToArray());
                command.Parameters.AddWithValue("birth", NpgsqlDbType.Date, (object?)baby.Baby.BirthDate ?? DBNull.Value);
                command.Parameters.AddWithValue("due", NpgsqlDbType.Date, (object?)baby.Baby.DueDate ?? DBNull.Value);
                break;
            case HouseholdSettingsWrite settings:
                command.CommandText =
                    """
                    INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq)
                    VALUES (@household, @unit, @zone, @hidden, @wall, @counter, @device, false, @seq)
                    ON CONFLICT (household_id) DO UPDATE SET volume_unit = excluded.volume_unit, time_zone_id = excluded.time_zone_id,
                        hidden_kinds = excluded.hidden_kinds, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = false, seq = excluded.seq
                    WHERE (household_settings.version_wall_ms, household_settings.version_counter, household_settings.version_device)
                        < (excluded.version_wall_ms, excluded.version_counter, excluded.version_device);
                    """;
                command.Parameters.AddWithValue("unit", settings.Settings.VolumeUnit == Coo.Domain.Households.VolumeUnit.Ounces ? "oz" : "ml");
                command.Parameters.AddWithValue("zone", settings.Settings.TimeZoneId);
                command.Parameters.AddWithValue("hidden", settings.Settings.HiddenKinds.Select(k => k.Value).Order(StringComparer.Ordinal).ToArray());
                break;
            case DismissalWrite dismissal:
                command.CommandText =
                    """
                    INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted, seq)
                    VALUES (@id, @household, @a, @b, @wall, @counter, @device, false, @seq)
                    ON CONFLICT (id) DO UPDATE SET version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = false, seq = excluded.seq
                    WHERE duplicate_dismissals.household_id = excluded.household_id
                        AND (duplicate_dismissals.version_wall_ms, duplicate_dismissals.version_counter, duplicate_dismissals.version_device)
                            < (excluded.version_wall_ms, excluded.version_counter, excluded.version_device);
                    """;
                command.Parameters.AddWithValue("id", dismissal.Dismissal.Id.Value);
                command.Parameters.AddWithValue("a", dismissal.Dismissal.Pair.First.Value);
                command.Parameters.AddWithValue("b", dismissal.Dismissal.Pair.Second.Value);
                break;
            case TombstoneWrite:
                command.CommandText =
                    $"UPDATE {table.Name} SET is_deleted = true, version_wall_ms = @wall, version_counter = @counter, version_device = @device, seq = @seq "
                    + $"WHERE {table.Key} = @id AND household_id = @household "
                    + "AND (version_wall_ms, version_counter, version_device) < (@wall, @counter, @device);";
                command.Parameters.AddWithValue("id", write.EntityId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(write), write.GetType().Name, "Unknown write.");
        }

        if (write is not CareEntryWrite)
        {
            command.Parameters.AddWithValue("household", household.Value);
            command.Parameters.AddWithValue("wall", write.Version.WallClockMs);
            command.Parameters.AddWithValue("counter", write.Version.Counter);
            command.Parameters.AddWithValue("device", write.Version.DeviceId.Value);
            command.Parameters.AddWithValue("seq", seq);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task RecordConflictAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, Guid changeId, SyncWrite loser, HlcStamp winner,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO sync_conflicts (household_id, change_id, entity_type, entity_id, winner_wall_ms, winner_counter, winner_device,
                loser_wall_ms, loser_counter, loser_device, recorded_at)
            VALUES (@household, @change, @type, @entity, @ww, @wc, @wd, @lw, @lc, @ld, @at);
            """, connection, transaction);
        var p = command.Parameters;
        p.AddWithValue("household", household.Value);
        p.AddWithValue("change", changeId);
        p.AddWithValue("type", loser.EntityType);
        p.AddWithValue("entity", loser.EntityId);
        p.AddWithValue("ww", winner.WallClockMs);
        p.AddWithValue("wc", winner.Counter);
        p.AddWithValue("wd", winner.DeviceId.Value);
        p.AddWithValue("lw", loser.Version.WallClockMs);
        p.AddWithValue("lc", loser.Version.Counter);
        p.AddWithValue("ld", loser.Version.DeviceId.Value);
        p.AddWithValue("at", now.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>One statement: the primary key decides which attempt's result is the change's.</summary>
    private static async Task<bool> TryRecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid changeId, ChangeResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO applied_changes (change_id, result, reason, applied_at) VALUES (@id, @result, @reason, @at) ON CONFLICT (change_id) DO NOTHING;",
            connection, transaction);
        command.Parameters.AddWithValue("id", changeId);
        command.Parameters.AddWithValue("result", ResultText(result.Outcome));
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)result.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("at", now.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static string ResultText(ChangeOutcome outcome) => outcome switch
    {
        ChangeOutcome.Applied => ChangeResults.Applied,
        ChangeOutcome.Superseded => ChangeResults.Superseded,
        _ => ChangeResults.Rejected,
    };

    private static ChangeResult ToResult(string result, string? reason) => result switch
    {
        ChangeResults.Applied => ChangeResult.Applied,
        ChangeResults.Superseded => ChangeResult.Superseded,
        _ => ChangeResult.Rejected(reason ?? RejectionReasons.ChangeMalformed),
    };

    private static HlcStamp Stamp(NpgsqlDataReader r, int at) => new(r.GetInt64(at), r.GetInt32(at + 1), new DeviceId(r.GetGuid(at + 2)));

    private static async Task<List<PulledRecord>> ReadCareEntriesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, kind, subject_baby_id, subject_person, is_session, start_at, start_offset_min, end_at, end_offset_min, logged_by,
                source, source_prompt_version, note, details::text, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM care_entries WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var start = ServerColumns.ParseInstant(r.GetDateTime(5), r.GetInt16(6)) ?? ServerColumns.Utc(r.GetDateTime(5));
            DateTimeOffset? end = r.IsDBNull(7) ? null : ServerColumns.ParseInstant(r.GetDateTime(7), r.GetInt16(8)) ?? ServerColumns.Utc(r.GetDateTime(7));
            var payload = new CareEntryPayload(
                r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), start, end,
                r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13));
            list.Add(new PulledRecord(SyncEntityTypes.CareEntry, r.GetGuid(0), Stamp(r, 14), r.GetBoolean(17), r.GetInt64(18), payload));
        }

        return list;
    }

    private static async Task<List<PulledRecord>> ReadBabiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM baby_profiles WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var payload = new BabyProfilePayload(
                r.GetString(1), r.GetFieldValue<string[]>(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4));
            list.Add(new PulledRecord(SyncEntityTypes.BabyProfile, r.GetGuid(0), Stamp(r, 5), r.GetBoolean(8), r.GetInt64(9), payload));
        }

        return list;
    }

    private static async Task<List<PulledRecord>> ReadSettingsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM household_settings WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var payload = new HouseholdSettingsPayload(r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3));
            list.Add(new PulledRecord(SyncEntityTypes.HouseholdSettings, r.GetGuid(0), Stamp(r, 4), r.GetBoolean(7), r.GetInt64(8), payload));
        }

        return list;
    }

    private static async Task<List<PulledRecord>> ReadDismissalsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM duplicate_dismissals WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new PulledRecord(SyncEntityTypes.DuplicateDismissal, r.GetGuid(0), Stamp(r, 3), r.GetBoolean(6), r.GetInt64(7), new DismissalPayload(r.GetGuid(1), r.GetGuid(2))));

        return list;
    }

    private static void AddPage(NpgsqlCommand command, HouseholdId household, long cursor, int limit)
    {
        command.Parameters.AddWithValue("h", household.Value);
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("limit", limit);
    }
}
