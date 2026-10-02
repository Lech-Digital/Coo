using System.Globalization;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// Records changing household on this phone (Spec 011 §4, §11; ADR 0003 §7). Each operation is one transaction: a failure
/// part-way changes nothing. Versions never move: the moved records were never on the server, and the outbox names
/// records, so the next push sends their moved state.
/// </summary>
internal sealed class HouseholdMoves(LocalDatabase database) : IHouseholdMoves
{
    private const string ActiveKey = "active_household";

    public async Task<HouseholdId> ActiveHouseholdAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var named = (string?)await connection.ScalarAsync(
            """
            SELECT a.value FROM app_state a JOIN household_settings s ON s.household_id = a.value AND s.is_deleted = 0
            WHERE a.key = 'active_household';
            """,
            cancellationToken);
        if (Columns.ParseGuid(named) is { } active)
            return new HouseholdId(active);

        var first = (string?)await connection.ScalarAsync(
            "SELECT household_id FROM household_settings WHERE is_deleted = 0 ORDER BY household_id LIMIT 1;", cancellationToken)
            ?? throw new InvalidOperationException("There is no household on this phone: start-up creates one first.");
        await SetActiveAsync(connection, null, first, cancellationToken);
        return new HouseholdId(Guid.Parse(first));
    }

    public async Task<bool> MoveAllAsync(HouseholdId from, HouseholdId to, UserId user, CancellationToken cancellationToken)
    {
        if (from == to)
            return true;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var (f, t) = (Columns.Id(from.Value), Columns.Id(to.Value));
        if (await EverSyncedAsync(connection, transaction, f, cancellationToken))
        {
            await SetActiveAsync(connection, transaction, t, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        foreach (var table in new[] { "care_entries", "baby_profiles", "duplicate_dismissals" })
            await connection.ExecuteAsync($"UPDATE {table} SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));

        // The settings row is keyed by its household: the row and its outbox rows take the new ID.
        await connection.ExecuteAsync("UPDATE household_settings SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));
        await connection.ExecuteAsync(
            "UPDATE outbox SET entity_id = $to WHERE entity_type = 'HouseholdSettings' AND entity_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));
        await MoveBookkeepingAsync(connection, transaction, f, t, cancellationToken);
        await SyncStore.RewriteThisDeviceAsync(connection, transaction, to, user, cancellationToken);
        await SetActiveAsync(connection, transaction, t, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> HasEverSyncedAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await EverSyncedAsync(connection, null, Columns.Id(household.Value), cancellationToken);
    }

    /// <summary>Spec 011 §4: a cursor, or a record that is neither waiting in the outbox nor rejected, means it was on a server.</summary>
    private static async Task<bool> EverSyncedAsync(SqliteConnection connection, SqliteTransaction? transaction, string household, CancellationToken cancellationToken)
    {
        var result = await connection.ScalarAsync(
            """
            SELECT EXISTS (SELECT 1 FROM sync_cursors WHERE household_id = $h)
                OR EXISTS (SELECT 1 FROM care_entries r WHERE r.household_id = $h
                    AND NOT EXISTS (SELECT 1 FROM outbox o WHERE o.entity_type = 'CareEntry' AND o.entity_id = r.id)
                    AND NOT EXISTS (SELECT 1 FROM sync_rejections j WHERE j.entity_type = 'CareEntry' AND j.entity_id = r.id))
                OR EXISTS (SELECT 1 FROM baby_profiles r WHERE r.household_id = $h
                    AND NOT EXISTS (SELECT 1 FROM outbox o WHERE o.entity_type = 'BabyProfile' AND o.entity_id = r.id)
                    AND NOT EXISTS (SELECT 1 FROM sync_rejections j WHERE j.entity_type = 'BabyProfile' AND j.entity_id = r.id))
                OR EXISTS (SELECT 1 FROM duplicate_dismissals r WHERE r.household_id = $h
                    AND NOT EXISTS (SELECT 1 FROM outbox o WHERE o.entity_type = 'DuplicateDismissal' AND o.entity_id = r.id)
                    AND NOT EXISTS (SELECT 1 FROM sync_rejections j WHERE j.entity_type = 'DuplicateDismissal' AND j.entity_id = r.id));
            """,
            cancellationToken, transaction, ("$h", household));
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    public async Task<LocalSummary> SummaryAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        var count = Convert.ToInt32(
            await connection.ScalarAsync("SELECT count(*) FROM care_entries WHERE household_id = $h AND is_deleted = 0;", cancellationToken, null, h),
            CultureInfo.InvariantCulture);
        var babies = new List<LocalBaby>();
        await using var command = connection.Command(
            "SELECT id, name FROM baby_profiles WHERE household_id = $h AND is_deleted = 0 ORDER BY coalesce(birth_date, due_date), name;", null, h);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(0)) is { } id)
                babies.Add(new LocalBaby(new BabyId(id), reader.GetString(1)));
        }

        return new LocalSummary(count, babies);
    }

    public async Task<bool> BringOverAsync(BringOverPlan plan, UserId user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var (f, t) = (Columns.Id(plan.From.Value), Columns.Id(plan.To.Value));
        if (await EverSyncedAsync(connection, transaction, f, cancellationToken))
        {
            await SetActiveAsync(connection, transaction, t, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        foreach (var (local, target) in plan.Babies)
        {
            var l = Columns.Id(local.Value);
            if (target is { } mapped)
            {
                // A mapped baby: its entries point at the family's baby; its own profile and outbox rows go (the household's win).
                await connection.ExecuteAsync(
                    "UPDATE care_entries SET subject_baby_id = $target WHERE household_id = $from AND subject_baby_id = $local;",
                    cancellationToken, transaction, ("$target", Columns.Id(mapped.Value)), ("$from", f), ("$local", l));
                await connection.ExecuteAsync("DELETE FROM outbox WHERE entity_type = 'BabyProfile' AND entity_id = $local;", cancellationToken, transaction, ("$local", l));
                await connection.ExecuteAsync("DELETE FROM sync_rejections WHERE entity_type = 'BabyProfile' AND entity_id = $local;", cancellationToken, transaction, ("$local", l));
                await connection.ExecuteAsync("DELETE FROM baby_profiles WHERE id = $local AND household_id = $from;", cancellationToken, transaction, ("$local", l), ("$from", f));
            }
            else
            {
                // Add as a new baby: the profile moves with its outbox row.
                await connection.ExecuteAsync("UPDATE baby_profiles SET household_id = $to WHERE id = $local AND household_id = $from;", cancellationToken, transaction, ("$to", t), ("$local", l), ("$from", f));
            }
        }

        // A baby the plan does not name is added as new, never left behind.
        await connection.ExecuteAsync("UPDATE baby_profiles SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));
        await connection.ExecuteAsync("UPDATE care_entries SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));
        await connection.ExecuteAsync("UPDATE duplicate_dismissals SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));

        // The provisional settings and their outbox rows go: the household's settings win.
        await connection.ExecuteAsync("DELETE FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = $from;", cancellationToken, transaction, ("$from", f));
        await connection.ExecuteAsync("DELETE FROM sync_rejections WHERE entity_type = 'HouseholdSettings' AND entity_id = $from;", cancellationToken, transaction, ("$from", f));
        await connection.ExecuteAsync("DELETE FROM household_settings WHERE household_id = $from;", cancellationToken, transaction, ("$from", f));

        await MoveBookkeepingAsync(connection, transaction, f, t, cancellationToken);
        await SyncStore.RewriteThisDeviceAsync(connection, transaction, plan.To, user, cancellationToken);
        await SetActiveAsync(connection, transaction, t, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FreshBootstrapAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        var pending = Convert.ToInt64(
            await connection.ScalarAsync($"SELECT count(*) FROM outbox o WHERE {SyncStore.InHousehold};", cancellationToken, transaction, h),
            CultureInfo.InvariantCulture);
        var rejected = Convert.ToInt64(
            await connection.ScalarAsync("SELECT count(*) FROM sync_rejections WHERE household_id = $h;", cancellationToken, transaction, h),
            CultureInfo.InvariantCulture);

        // A rejected record is not on the server: clearing it would lose it.
        if (pending > 0 || rejected > 0)
            return false;

        foreach (var table in new[] { "care_entries", "baby_profiles", "duplicate_dismissals", "sync_rejections" })
            await connection.ExecuteAsync($"DELETE FROM {table} WHERE household_id = $h;", cancellationToken, transaction, h);

        // A cursor of 0 with no head: the phone is empty now, so the server may skip tombstones (ruling 20).
        await connection.ExecuteAsync(
            """
            INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ($h, 0, NULL)
            ON CONFLICT (household_id) DO UPDATE SET last_seq = 0, bootstrap_head = NULL;
            """,
            cancellationToken, transaction, h);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        await connection.ExecuteAsync($"DELETE FROM outbox AS o WHERE {SyncStore.InHousehold};", cancellationToken, transaction, h);
        foreach (var table in new[] { "care_entries", "baby_profiles", "duplicate_dismissals", "household_settings", "sync_rejections", "sync_cursors" })
            await connection.ExecuteAsync($"DELETE FROM {table} WHERE household_id = $h;", cancellationToken, transaction, h);
        await connection.ExecuteAsync(
            "DELETE FROM app_state WHERE key = 'softlock:' || $h OR (key = 'active_household' AND value = $h);", cancellationToken, transaction, h);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Rejections follow their records; both households' cursors go, so the phone, which now holds records of the target,
    /// starts its next pull at head 0 and sees every tombstone (ruling 20). The pull then re-fetches the household.
    /// </summary>
    private static async Task MoveBookkeepingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string from, string to, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync("UPDATE sync_rejections SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", to), ("$from", from));
        await connection.ExecuteAsync("DELETE FROM sync_cursors WHERE household_id IN ($from, $to);", cancellationToken, transaction, ("$to", to), ("$from", from));
    }

    private static async Task SetActiveAsync(SqliteConnection connection, SqliteTransaction? transaction, string household, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($key, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$key", ActiveKey), ("$v", household));
    }
}
