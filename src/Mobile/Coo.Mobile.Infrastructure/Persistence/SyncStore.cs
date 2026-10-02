using System.Globalization;
using System.Text.Json;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// Sync on the phone (Spec 011 §6). The outbox names records; a change is the record's current state, read now (Spec 004
/// §4.3). Results and pulled pages are each one transaction; a page commits together with its cursor. Pulled details
/// are stored exactly as received and classified by the read guard (ADR 0010 §3); a pulled record leaves no outbox row.
/// </summary>
internal sealed class SyncStore(LocalDatabase database) : ISyncStore
{
    /// <summary>The outbox rows whose record belongs to <c>$h</c>; settings are keyed by the household itself.</summary>
    internal const string InHousehold =
        """
        ((o.entity_type = 'CareEntry' AND EXISTS (SELECT 1 FROM care_entries c WHERE c.id = o.entity_id AND c.household_id = $h))
         OR (o.entity_type = 'BabyProfile' AND EXISTS (SELECT 1 FROM baby_profiles b WHERE b.id = o.entity_id AND b.household_id = $h))
         OR (o.entity_type = 'HouseholdSettings' AND o.entity_id = $h AND EXISTS (SELECT 1 FROM household_settings s WHERE s.household_id = $h))
         OR (o.entity_type = 'DuplicateDismissal' AND EXISTS (SELECT 1 FROM duplicate_dismissals d WHERE d.id = o.entity_id AND d.household_id = $h)))
        """;

    public async Task PrepareForPushAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RewriteThisDeviceAsync(connection, transaction, household, user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The server rejects <c>this-device</c> (Spec 011 §5); these records were never on the server, so no version moves.</summary>
    internal static async Task RewriteThisDeviceAsync(
        SqliteConnection connection, SqliteTransaction transaction, HouseholdId household, UserId user, CancellationToken cancellationToken)
    {
        var person = Columns.Person(Coo.Domain.CareLog.PersonRef.User(user));
        var h = Columns.Id(household.Value);
        await connection.ExecuteAsync(
            "UPDATE care_entries SET logged_by = $person WHERE household_id = $h AND logged_by = 'this-device';",
            cancellationToken, transaction, ("$person", person), ("$h", h));
        await connection.ExecuteAsync(
            "UPDATE care_entries SET subject_person = $person WHERE household_id = $h AND subject_person = 'this-device';",
            cancellationToken, transaction, ("$person", person), ("$h", h));
    }

    public async Task<IReadOnlyList<OutgoingChange>> NextBatchAsync(HouseholdId household, int max, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        while (true)
        {
            var groups = new List<(string Type, string Id, long LastSeq)>();
            await using (var command = connection.Command(
                $"""
                SELECT o.entity_type, o.entity_id, max(o.seq) FROM outbox o WHERE {InHousehold}
                GROUP BY o.entity_type, o.entity_id ORDER BY min(o.seq) LIMIT $max;
                """,
                null, ("$h", Columns.Id(household.Value)), ("$max", max)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    groups.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }

            var changes = new List<OutgoingChange>(groups.Count);
            var dropped = false;
            foreach (var (type, id, lastSeq) in groups)
            {
                var changeId = (string)(await connection.ScalarAsync("SELECT change_id FROM outbox WHERE seq = $seq;", cancellationToken, null, ("$seq", lastSeq)))!;
                if (await ReadStateAsync(connection, type, id, cancellationToken) is not { } state)
                {
                    // A record whose stored state cannot be read can never be sent; its rows must not hold a batch slot forever.
                    await connection.ExecuteAsync(
                        "DELETE FROM outbox WHERE entity_type = $type AND entity_id = $id AND seq <= $last;",
                        cancellationToken, null, ("$type", type), ("$id", id), ("$last", lastSeq));
                    dropped = true;
                    continue;
                }

                changes.Add(new OutgoingChange(
                    Guid.Parse(changeId), type, Guid.Parse(id), state.IsDeleted, state.Version, lastSeq, state.IsDeleted ? null : state.Payload));
            }

            if (!dropped || groups.Count < max)
                return changes;
        }
    }

    public async Task ApplyResultsAsync(
        HouseholdId household, IReadOnlyList<OutgoingChange> changes, IReadOnlyList<PushResult> results, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(results);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var result in results)
        {
            if (changes.FirstOrDefault(c => c.ChangeId == result.ChangeId) is not { } change)
                continue;
            var key = new[] { ("$type", (object?)change.EntityType), ("$id", Columns.Id(change.EntityId)) };

            // A final result: this record's rows up to the change go; a later edit's row stays (ADR 0002 §2).
            await connection.ExecuteAsync(
                "DELETE FROM outbox WHERE entity_type = $type AND entity_id = $id AND seq <= $last;",
                cancellationToken, transaction, [.. key, ("$last", change.LastOutboxSeq)]);

            if (result.Outcome == PushOutcome.Rejected)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ($type, $id, $h, $reason, $at)
                    ON CONFLICT (entity_type, entity_id) DO UPDATE SET household_id = excluded.household_id, reason = excluded.reason,
                        rejected_utc_ms = excluded.rejected_utc_ms;
                    """,
                    cancellationToken, transaction,
                    [.. key, ("$h", Columns.Id(household.Value)), ("$reason", string.IsNullOrEmpty(result.Reason) ? RejectionReasons.ChangeMalformed : result.Reason),
                        ("$at", now.ToUnixTimeMilliseconds())]);
            }
            else
            {
                await connection.ExecuteAsync("DELETE FROM sync_rejections WHERE entity_type = $type AND entity_id = $id;", cancellationToken, transaction, key);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SyncCursor> GetCursorAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        await using (var command = connection.Command("SELECT last_seq, bootstrap_head FROM sync_cursors WHERE household_id = $h;", null, h))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            // (0, null) is what an empty first pull stores: it still has to meet the holds-records rule below.
            if (await reader.ReadAsync(cancellationToken) && !(reader.GetInt64(0) == 0 && reader.IsDBNull(1)))
                return new SyncCursor(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
        }

        // Ruling 20: a phone that holds records of this household must see every tombstone on its first pull.
        var holds = await connection.ScalarAsync(
            """
            SELECT EXISTS (SELECT 1 FROM care_entries WHERE household_id = $h) OR EXISTS (SELECT 1 FROM baby_profiles WHERE household_id = $h)
                OR EXISTS (SELECT 1 FROM duplicate_dismissals WHERE household_id = $h);
            """,
            cancellationToken, null, h);
        return new SyncCursor(0, Convert.ToInt64(holds, CultureInfo.InvariantCulture) == 1 ? 0 : null);
    }

    public async Task ApplyPageAsync(HouseholdId household, PulledPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var record in page.Records)
        {
            if (await BelongsToAnotherHouseholdAsync(connection, transaction, household, record, cancellationToken))
                continue;
            if (PullRule.KeepsLocal(await PendingVersionAsync(connection, transaction, record.EntityType, record.EntityId, cancellationToken), record.Version))
                continue;
            await UpsertAsync(connection, transaction, household, record, cancellationToken);

            // The pulled state wins: this record's pending rows are older than it, and a refused local change no longer exists.
            var key = new[] { ("$type", (object?)record.EntityType), ("$id", Columns.Id(record.EntityId)) };
            await connection.ExecuteAsync("DELETE FROM outbox WHERE entity_type = $type AND entity_id = $id;", cancellationToken, transaction, key);
            await connection.ExecuteAsync("DELETE FROM sync_rejections WHERE entity_type = $type AND entity_id = $id;", cancellationToken, transaction, key);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ($h, $seq, $head)
            ON CONFLICT (household_id) DO UPDATE SET last_seq = excluded.last_seq, bootstrap_head = excluded.bootstrap_head;
            """,
            cancellationToken, transaction,
            ("$h", Columns.Id(household.Value)), ("$seq", page.Cursor), ("$head", page.HasMore ? page.BootstrapHead : null));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SyncCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        var cursor = await connection.ScalarAsync("SELECT last_seq FROM sync_cursors WHERE household_id = $h;", cancellationToken, null, h);
        var outbox = await connection.ScalarAsync(
            $"SELECT count(*) FROM (SELECT DISTINCT o.entity_type, o.entity_id FROM outbox o WHERE {InHousehold});", cancellationToken, null, h);
        var rejections = await connection.ScalarAsync("SELECT count(*) FROM sync_rejections WHERE household_id = $h;", cancellationToken, null, h);
        return new SyncCounts(
            cursor is null ? 0 : Convert.ToInt64(cursor, CultureInfo.InvariantCulture),
            Convert.ToInt32(outbox, CultureInfo.InvariantCulture),
            Convert.ToInt32(rejections, CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<SyncRejection>> ListRejectionsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.Command(
            "SELECT entity_type, entity_id, reason, rejected_utc_ms FROM sync_rejections WHERE household_id = $h ORDER BY rejected_utc_ms DESC, entity_id;",
            null, ("$h", Columns.Id(household.Value)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<SyncRejection>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(1)) is { } id)
                list.Add(new SyncRejection(reader.GetString(0), id, reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        }

        return list;
    }

    private sealed record State(HlcStamp Version, bool IsDeleted, RecordPayload Payload);

    private static async Task<State?> ReadStateAsync(SqliteConnection connection, string type, string id, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case SyncEntityTypes.CareEntry:
                {
                    var rows = await CareLogStore.ReadRowsAsync(connection, $"SELECT {CareEntryRow.SelectColumns} FROM care_entries WHERE id = $id;", cancellationToken, ("$id", id));
                    if (rows.Count == 0 || Columns.ParseStamp(rows[0].VersionWallMs, rows[0].VersionCounter, rows[0].VersionDevice) is not { } version)
                        return null;
                    var r = rows[0];
                    var start = Columns.ParseInstant(r.StartUtcMs, r.StartOffsetMin) ?? DateTimeOffset.FromUnixTimeMilliseconds(r.StartUtcMs);
                    var end = r.EndUtcMs is { } endMs ? Columns.ParseInstant(endMs, r.EndOffsetMin ?? 0) ?? DateTimeOffset.FromUnixTimeMilliseconds(endMs) : (DateTimeOffset?)null;
                    return new State(version, r.IsDeleted, new CareEntryPayload(
                        r.Kind, Columns.ParseGuid(r.SubjectBabyId), r.SubjectPerson, r.IsSession, start, end, r.LoggedBy, r.Source, r.SourcePromptVersion,
                        r.Note, r.Details));
                }

            case SyncEntityTypes.BabyProfile:
                {
                    await using var command = connection.Command(
                        "SELECT name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted FROM baby_profiles WHERE id = $id;",
                        null, ("$id", id));
                    await using var r = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await r.ReadAsync(cancellationToken) || Columns.ParseStamp(r.GetInt64(4), r.GetInt64(5), r.GetString(6)) is not { } version)
                        return null;
                    return new State(version, r.GetInt64(7) == 1, new BabyProfilePayload(
                        r.GetString(0), JsonSerializer.Deserialize(r.GetString(1), StringListJson.Default.ListString) ?? [],
                        r.IsDBNull(2) ? null : DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        r.IsDBNull(3) ? null : DateOnly.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
                }

            case SyncEntityTypes.HouseholdSettings:
                {
                    await using var command = connection.Command(
                        "SELECT volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted FROM household_settings WHERE household_id = $id;",
                        null, ("$id", id));
                    await using var r = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await r.ReadAsync(cancellationToken) || Columns.ParseStamp(r.GetInt64(3), r.GetInt64(4), r.GetString(5)) is not { } version)
                        return null;
                    return new State(version, r.GetInt64(6) == 1, new HouseholdSettingsPayload(
                        r.GetString(0), r.GetString(1), JsonSerializer.Deserialize(r.GetString(2), StringListJson.Default.ListString) ?? []));
                }

            case SyncEntityTypes.DuplicateDismissal:
                {
                    await using var command = connection.Command(
                        "SELECT entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted FROM duplicate_dismissals WHERE id = $id;",
                        null, ("$id", id));
                    await using var r = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await r.ReadAsync(cancellationToken) || Columns.ParseStamp(r.GetInt64(2), r.GetInt64(3), r.GetString(4)) is not { } version)
                        return null;
                    return new State(version, r.GetInt64(5) == 1, new DismissalPayload(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));
                }

            default:
                return null;
        }
    }

    /// <summary>A local row with this ID that belongs to another household is never touched by a pull.</summary>
    private static async Task<bool> BelongsToAnotherHouseholdAsync(
        SqliteConnection connection, SqliteTransaction transaction, HouseholdId household, PulledRecord record, CancellationToken cancellationToken)
    {
        var table = record.EntityType switch
        {
            SyncEntityTypes.CareEntry => "care_entries",
            SyncEntityTypes.BabyProfile => "baby_profiles",
            SyncEntityTypes.DuplicateDismissal => "duplicate_dismissals",
            _ => null,
        };
        if (table is null)
            return false;
        var other = await connection.ScalarAsync(
            $"SELECT EXISTS (SELECT 1 FROM {table} WHERE id = $id AND household_id <> $h);", cancellationToken, transaction,
            ("$id", Columns.Id(record.EntityId)), ("$h", Columns.Id(household.Value)));
        return Convert.ToInt64(other, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>The local version when the record has an outbox row: that is a pending change (ADR 0002 §6).</summary>
    private static async Task<HlcStamp?> PendingVersionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string type, Guid id, CancellationToken cancellationToken)
    {
        var key = Columns.Id(id);
        var pending = await connection.ScalarAsync(
            "SELECT EXISTS (SELECT 1 FROM outbox WHERE entity_type = $type AND entity_id = $id);", cancellationToken, transaction, ("$type", type), ("$id", key));
        if (Convert.ToInt64(pending, CultureInfo.InvariantCulture) == 0)
            return null;

        var (table, column) = type switch
        {
            SyncEntityTypes.CareEntry => ("care_entries", "id"),
            SyncEntityTypes.BabyProfile => ("baby_profiles", "id"),
            SyncEntityTypes.HouseholdSettings => ("household_settings", "household_id"),
            _ => ("duplicate_dismissals", "id"),
        };
        await using var command = connection.Command(
            $"SELECT version_wall_ms, version_counter, version_device FROM {table} WHERE {column} = $id;", transaction, ("$id", key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Columns.ParseStamp(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)) : null;
    }

    private static Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, HouseholdId household, PulledRecord record, CancellationToken cancellationToken)
    {
        var h = Columns.Id(household.Value);
        var id = Columns.Id(record.EntityId);
        var deleted = record.IsDeleted ? 1 : 0;
        switch (record.Payload)
        {
            case CareEntryPayload c:
                {
                    var (startMs, startOffset) = Columns.Instant(c.StartAt);
                    var endParts = c.EndAt is { } e ? Columns.Instant(e) : ((long, int)?)null;
                    var row = new CareEntryRow(
                        id, h, c.Kind, c.SubjectBabyId is { } b ? Columns.Id(b) : null, c.SubjectPerson, c.IsSession, startMs, startOffset,
                        endParts?.Item1, endParts?.Item2, c.LoggedBy, c.Source, c.SourcePromptVersion, c.Note, c.DetailsJson,
                        record.Version.WallClockMs, record.Version.Counter, Columns.Id(record.Version.DeviceId.Value), record.IsDeleted, CareEntryClassifier.Ok);
                    var classified = CareEntryClassifier.Classify(row);
                    return connection.ExecuteAsync(
                        """
                    INSERT INTO care_entries (id, household_id, kind, subject_baby_id, subject_person, is_session, start_utc_ms, start_offset_min,
                        end_utc_ms, end_offset_min, logged_by, source, source_prompt_version, note, details, version_wall_ms, version_counter,
                        version_device, is_deleted, integrity, integrity_reason)
                    VALUES ($id, $h, $kind, $baby, $person, $session, $start, $start_offset, $end, $end_offset, $logged_by, $source, $prompt, $note,
                        $details, $version_wall_ms, $version_counter, $version_device, $deleted, $integrity, $reason)
                    ON CONFLICT (id) DO UPDATE SET household_id = excluded.household_id, kind = excluded.kind, subject_baby_id = excluded.subject_baby_id,
                        subject_person = excluded.subject_person, is_session = excluded.is_session, start_utc_ms = excluded.start_utc_ms,
                        start_offset_min = excluded.start_offset_min, end_utc_ms = excluded.end_utc_ms, end_offset_min = excluded.end_offset_min,
                        logged_by = excluded.logged_by, source = excluded.source, source_prompt_version = excluded.source_prompt_version,
                        note = excluded.note, details = excluded.details, version_wall_ms = excluded.version_wall_ms,
                        version_counter = excluded.version_counter, version_device = excluded.version_device, is_deleted = excluded.is_deleted,
                        integrity = excluded.integrity, integrity_reason = excluded.integrity_reason;
                    """,
                        cancellationToken, transaction,
                        [
                            ("$id", id), ("$h", h), ("$kind", c.Kind), ("$baby", row.SubjectBabyId), ("$person", c.SubjectPerson),
                        ("$session", c.IsSession ? 1 : 0), ("$start", startMs), ("$start_offset", startOffset), ("$end", endParts?.Item1),
                        ("$end_offset", endParts?.Item2), ("$logged_by", c.LoggedBy), ("$source", c.Source), ("$prompt", c.SourcePromptVersion),
                        ("$note", c.Note), ("$details", c.DetailsJson), .. Columns.Stamp(record.Version), ("$deleted", deleted),
                        ("$integrity", classified.Integrity), ("$reason", classified.Reason),
                        ]);
                }

            case BabyProfilePayload b:
                return connection.ExecuteAsync(
                    """
                    INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted)
                    VALUES ($id, $h, $name, $nicknames, $birth, $due, $version_wall_ms, $version_counter, $version_device, $deleted)
                    ON CONFLICT (id) DO UPDATE SET household_id = excluded.household_id, name = excluded.name, nicknames = excluded.nicknames,
                        birth_date = excluded.birth_date, due_date = excluded.due_date, version_wall_ms = excluded.version_wall_ms,
                        version_counter = excluded.version_counter, version_device = excluded.version_device, is_deleted = excluded.is_deleted;
                    """,
                    cancellationToken, transaction,
                    [
                        ("$id", id), ("$h", h), ("$name", b.Name), ("$nicknames", JsonSerializer.Serialize(b.Nicknames.ToList(), StringListJson.Default.ListString)),
                        ("$birth", b.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        ("$due", b.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), .. Columns.Stamp(record.Version), ("$deleted", deleted),
                    ]);

            case HouseholdSettingsPayload s:
                return connection.ExecuteAsync(
                    """
                    INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted)
                    VALUES ($h, $unit, $zone, $hidden, $version_wall_ms, $version_counter, $version_device, $deleted)
                    ON CONFLICT (household_id) DO UPDATE SET volume_unit = excluded.volume_unit, time_zone_id = excluded.time_zone_id,
                        hidden_kinds = excluded.hidden_kinds, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = excluded.is_deleted;
                    """,
                    cancellationToken, transaction,
                    [
                        ("$h", h), ("$unit", s.VolumeUnit), ("$zone", s.TimeZoneId),
                        ("$hidden", JsonSerializer.Serialize(s.HiddenKinds.Order(StringComparer.Ordinal).ToList(), StringListJson.Default.ListString)),
                        .. Columns.Stamp(record.Version), ("$deleted", deleted),
                    ]);

            case DismissalPayload d:
                {
                    var (a, b) = (Columns.Id(d.EntryA), Columns.Id(d.EntryB));
                    if (string.CompareOrdinal(a, b) > 0)
                        (a, b) = (b, a);
                    return connection.ExecuteAsync(
                        """
                    INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted)
                    VALUES ($id, $h, $a, $b, $version_wall_ms, $version_counter, $version_device, $deleted)
                    ON CONFLICT (id) DO UPDATE SET household_id = excluded.household_id, version_wall_ms = excluded.version_wall_ms,
                        version_counter = excluded.version_counter, version_device = excluded.version_device, is_deleted = excluded.is_deleted;
                    """,
                        cancellationToken, transaction,
                        [("$id", id), ("$h", h), ("$a", a), ("$b", b), .. Columns.Stamp(record.Version), ("$deleted", deleted)]);
                }

            default:
                return Task.CompletedTask;
        }
    }
}
