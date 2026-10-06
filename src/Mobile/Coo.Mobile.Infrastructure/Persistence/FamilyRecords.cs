using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Whole-family operations (Spec 011b §6.2, §7.2, §8.2, §8.4), each one SQLite transaction.</summary>
internal sealed class FamilyRecords(LocalDatabase database, TimeProvider time, LocalWrites writes) : IFamilyRecords
{
    public async Task<int> CountRecordsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var count = await connection.ScalarAsync(
            """
            SELECT (SELECT count(*) FROM care_entries WHERE household_id = $h) + (SELECT count(*) FROM baby_profiles WHERE household_id = $h)
                + (SELECT count(*) FROM duplicate_dismissals WHERE household_id = $h);
            """,
            cancellationToken, null, ("$h", Columns.Id(household.Value)));
        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }

    public async Task<CopyResult?> CopyAsync(BringOverPlan plan, UserId user, HlcStamp firstStamp, int reservedCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.From == plan.To)
            throw new ArgumentException("A family cannot be copied into itself.", nameof(plan));
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var marker = FamilyStore.CopiedKey(plan.From, plan.To);
        if (await connection.ScalarAsync("SELECT 1 FROM app_state WHERE key = $k;", cancellationToken, transaction, ("$k", marker)) is not null)
            return null;

        var (source, target) = (Columns.Id(plan.From.Value), Columns.Id(plan.To.Value));
        var person = Columns.Person(PersonRef.User(user));
        var counter = firstStamp.Counter;

        // The caller reserved reservedCount stamps on the clock before this transaction; a pull in between may have added records.
        // Using more would stamp past the clock, so the copy stops and the transaction rolls back with nothing written.
        HlcStamp Next() => counter - firstStamp.Counter >= reservedCount
            ? throw new StampBudgetExceededException()
            : new(firstStamp.WallClockMs, counter++, firstStamp.DeviceId);
        string NewId() => Columns.Id(Guid.CreateVersion7(time.GetUtcNow()));

        // 1. Babies: mapped ones point at the target's; the others become new profiles (spec §6.2's table).
        var targetBabies = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.Command("SELECT id FROM baby_profiles WHERE household_id = $t AND is_deleted = 0;", transaction, ("$t", target)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                targetBabies.Add(reader.GetString(0));
        }

        var sourceBabies = new List<(string Id, string Name, string Nicknames, string? Birth, string? Due)>();
        await using (var command = connection.Command(
            "SELECT id, name, nicknames, birth_date, due_date FROM baby_profiles WHERE household_id = $s AND is_deleted = 0 ORDER BY id;", transaction, ("$s", source)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sourceBabies.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var babyMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var baby in sourceBabies)
        {
            if (Columns.ParseGuid(baby.Id) is { } id && plan.Babies.TryGetValue(new BabyId(id), out var chosen) && chosen is { } mapped
                && targetBabies.Contains(Columns.Id(mapped.Value)))
            {
                babyMap[baby.Id] = Columns.Id(mapped.Value);
                continue;
            }

            var newId = NewId();
            var stamp = Next();
            await connection.ExecuteAsync(
                """
                INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted)
                VALUES ($id, $t, $name, $nicknames, $birth, $due, $version_wall_ms, $version_counter, $version_device, 0);
                """,
                cancellationToken, transaction,
                [("$id", newId), ("$t", target), ("$name", baby.Name), ("$nicknames", baby.Nicknames), ("$birth", baby.Birth), ("$due", baby.Due), .. Columns.Stamp(stamp)]);
            await Outbox.AppendAsync(connection, transaction, SyncedEntityType.BabyProfile, Guid.Parse(newId), false, stamp, time, cancellationToken);
            babyMap[baby.Id] = newId;
        }

        // 2. Care entries: live and readable ones only (spec §15.2 Q9); tombstones are neither copied nor counted (ruling 9).
        var rows = new List<CareEntryRow>();
        await using (var command = connection.Command(
            $"SELECT {CareEntryRow.SelectColumns} FROM care_entries WHERE household_id = $s ORDER BY start_utc_ms, id;", transaction, ("$s", source)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(CareEntryRow.Read(reader));
        }

        var (copied, skipped) = (0, 0);
        var entryMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.IsDeleted)
                continue;
            string? subjectBaby = null;
            if (CareEntryClassifier.Classify(row).Integrity != CareEntryClassifier.Ok
                || (row.SubjectBabyId is { } baby && !babyMap.TryGetValue(baby, out subjectBaby)))
            {
                skipped++;
                continue;
            }

            var newId = NewId();
            var stamp = Next();
            await connection.ExecuteAsync(
                """
                INSERT INTO care_entries (id, household_id, kind, subject_baby_id, subject_person, is_session, start_utc_ms, start_offset_min,
                    end_utc_ms, end_offset_min, logged_by, source, source_prompt_version, note, details, version_wall_ms, version_counter,
                    version_device, is_deleted, integrity, integrity_reason)
                VALUES ($id, $t, $kind, $baby, $person, $session, $start, $start_offset, $end, $end_offset, $logged_by, $source, $prompt, $note,
                    $details, $version_wall_ms, $version_counter, $version_device, 0, 'ok', NULL);
                """,
                cancellationToken, transaction,
                [
                    ("$id", newId), ("$t", target), ("$kind", row.Kind), ("$baby", subjectBaby),
                    ("$person", row.SubjectPerson == Columns.ThisDevice ? person : row.SubjectPerson),
                    ("$session", row.IsSession ? 1 : 0), ("$start", row.StartUtcMs), ("$start_offset", row.StartOffsetMin), ("$end", row.EndUtcMs),
                    ("$end_offset", row.EndOffsetMin), ("$logged_by", row.LoggedBy == Columns.ThisDevice ? person : row.LoggedBy), ("$source", row.Source),
                    ("$prompt", row.SourcePromptVersion), ("$note", row.Note), ("$details", row.Details), .. Columns.Stamp(stamp),
                ]);
            await Outbox.AppendAsync(connection, transaction, SyncedEntityType.CareEntry, Guid.Parse(newId), false, stamp, time, cancellationToken);
            entryMap[row.Id] = newId;
            copied++;
        }

        // 3. Dismissals whose two entries were both copied pair the two copies.
        var dismissals = new List<(string A, string B)>();
        await using (var command = connection.Command(
            "SELECT entry_a, entry_b FROM duplicate_dismissals WHERE household_id = $s AND is_deleted = 0 ORDER BY id;", transaction, ("$s", source)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                dismissals.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (a, b) in dismissals)
        {
            if (!entryMap.TryGetValue(a, out var copyA) || !entryMap.TryGetValue(b, out var copyB))
                continue;
            if (string.CompareOrdinal(copyA, copyB) > 0)
                (copyA, copyB) = (copyB, copyA);
            var newId = NewId();
            var stamp = Next();
            await connection.ExecuteAsync(
                """
                INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted)
                VALUES ($id, $t, $a, $b, $version_wall_ms, $version_counter, $version_device, 0);
                """,
                cancellationToken, transaction, [("$id", newId), ("$t", target), ("$a", copyA), ("$b", copyB), .. Columns.Stamp(stamp)]);
            await Outbox.AppendAsync(connection, transaction, SyncedEntityType.DuplicateDismissal, Guid.Parse(newId), false, stamp, time, cancellationToken);
        }

        // 4. The marker in the same transaction (a double tap cannot copy twice), and the target becomes the shown family.
        await connection.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ($k, $v);", cancellationToken, transaction,
            ("$k", marker), ("$v", time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        await HouseholdMoves.SetActiveAsync(connection, transaction, target, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        writes.Notify();
        return new CopyResult(copied, skipped);
    }

    public async Task DeleteAndReplaceAsync(HouseholdId household, HouseholdSettings replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await HouseholdMoves.DeleteHouseholdRowsAsync(connection, transaction, household, cancellationToken);
        await HouseholdStore.WriteSettingsAsync(connection, transaction, replacement, time, cancellationToken);
        await HouseholdMoves.SetActiveAsync(connection, transaction, Columns.Id(replacement.Id.Value), cancellationToken);

        // Final review C3: the one family a cycle may create on the server for an account left with none.
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$k", SyncState.ReplacementKey), ("$v", Columns.Id(replacement.Id.Value)));
        await transaction.CommitAsync(cancellationToken);
        writes.Notify();
    }

    public async Task<int> CountPendingAsync(IReadOnlyCollection<HouseholdId> households, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(households);
        await using var connection = await database.OpenAsync(cancellationToken);
        var total = 0;
        foreach (var household in households)
        {
            total += Convert.ToInt32(await connection.ScalarAsync(
                $"SELECT count(*) FROM (SELECT DISTINCT o.entity_type, o.entity_id FROM outbox o WHERE {SyncStore.InHousehold});",
                cancellationToken, null, ("$h", Columns.Id(household.Value))), CultureInfo.InvariantCulture);
        }

        return total;
    }

    public async Task<IReadOnlyList<StoredEntry>> ListUnsharedEntriesAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await CareLogStore.ReadRowsAsync(
            connection,
            $"""
            SELECT {CareEntryRow.SelectColumns} FROM care_entries c
            WHERE c.household_id = $h AND c.is_deleted = 0
              AND EXISTS (SELECT 1 FROM outbox o WHERE o.entity_type = 'CareEntry' AND o.entity_id = c.id)
            ORDER BY c.start_utc_ms, c.id;
            """,
            cancellationToken, ("$h", Columns.Id(household.Value)));
        return [.. rows.Select(r => CareEntryClassifier.Classify(r).Entry)];
    }
}
