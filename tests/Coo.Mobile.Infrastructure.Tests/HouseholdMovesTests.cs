using System.Text;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011 §4 and §11 on a real SQLite file: each move is one transaction, and a failure changes nothing.</summary>
public sealed class HouseholdMovesTests
{
    private static readonly HouseholdId Account = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly BabyId FamilyBaby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));

    /// <summary>This phone's provisional household with a baby, its settings, an entry logged before sign-in, and a dismissal.</summary>
    private static async Task<(TestDatabase Db, CareEntry Entry)> PhoneAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.Households.SaveSettingsAsync(HouseholdSettings.Create(TestDatabase.Household, VolumeUnit.Millilitres, "Europe/Amsterdam",
            await db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);
        await db.Households.SaveBabyAsync(BabyProfile.Create(TestDatabase.Baby, TestDatabase.Household, "Maja", [], new DateOnly(2026, 8, 20), null,
            await db.Device.NextAsync(CancellationToken.None), new DateOnly(2026, 10, 1)).Value, CancellationToken.None);
        var entry = CareEntry.Create(await db.NewIdAsync(), TestDatabase.Household, EntrySubject.Baby(TestDatabase.Baby),
            new Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Wet), EntryTiming.Point(db.Time.GetLocalNow()),
            PersonRef.ThisDevicesPerson, EntrySource.Manual, await db.Device.NextAsync(CancellationToken.None), db.Time.GetUtcNow()).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);
        await new DuplicateDismissalStore(db.Database, db.Time, db.Writes).SaveAsync(DuplicateDismissal.Create(
            new DismissalId(Guid.CreateVersion7(db.Time.GetUtcNow())), TestDatabase.Household, new EntryPair(entry.Id, await db.NewIdAsync()),
            await db.Device.NextAsync(CancellationToken.None)), CancellationToken.None);
        return (db, entry);
    }

    /// <summary>The family's household as the join-pending bootstrap pulled it: settings and a baby, no outbox rows.</summary>
    private static async Task PullFamilyAsync(TestDatabase db)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device)
            VALUES ('{Account.Value}', 'oz', 'Europe/Warsaw', '[]', 1, 0, '{TestDatabase.Baby.Value}');
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device)
            VALUES ('{FamilyBaby.Value}', '{Account.Value}', 'Maja', '[]', '2026-08-20', 1, 0, '{TestDatabase.Baby.Value}');
            """, CancellationToken.None);
    }

    private static async Task<string> SnapshotAsync(TestDatabase db)
    {
        var text = new StringBuilder();
        await using var connection = await db.OpenAsync();
        foreach (var table in new[] { "care_entries", "baby_profiles", "household_settings", "duplicate_dismissals", "outbox", "app_state", "sync_cursors", "sync_rejections" })
        {
            await using var command = connection.Command($"SELECT * FROM {table} ORDER BY rowid;");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    text.Append(reader.IsDBNull(i) ? "∅" : reader.GetValue(i)).Append('|');
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private static async Task BreakAsync(TestDatabase db, string trigger)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(trigger, CancellationToken.None);
    }

    [Fact]
    public async Task ActiveHouseholdAsync_NoneNamed_NamesTheFirstLiveOneAndKeepsIt()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;

        // Act
        var active = await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None);
        await PullFamilyAsync(db);
        var again = await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None);

        // Assert
        active.Should().Be(TestDatabase.Household);
        again.Should().Be(TestDatabase.Household, "a pulled household never takes over the screen by itself");
    }

    [Fact]
    public async Task MoveAllAsync_Created_EveryRecordMovesTheSettingsKeepTheirOutboxRowAndItBecomesActive()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var outboxBefore = await db.ScalarAsync("SELECT count(*) FROM outbox;");

        // Act
        await new HouseholdMoves(db.Database, db.Time).MoveAllAsync(TestDatabase.Household, Account, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND logged_by = 'user:{User.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(outboxBefore);
        (await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
        (await db.Store.FindAsync(entry.Id, CancellationToken.None)).Should().BeOfType<Coo.Mobile.Application.CareLog.LiveEntry>()
            .Which.Entry.Version.Should().Be(entry.Version, "these records were never on the server: no version moves");
    }

    [Fact]
    public async Task MoveAllAsync_AFailureHalfWay_ChangesNothing()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync(
            $"INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('CareEntry', '{entry.Id.Value}', '{TestDatabase.Household.Value}', 'x', 1);");
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE UPDATE ON household_settings BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);

        // Act
        var act = async () => await new HouseholdMoves(db.Database, db.Time).MoveAllAsync(TestDatabase.Household, Account, User, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task BringOverAsync_LocalBabyMappedToTheFamilys_EntriesFollowAndTheLocalProfileAndSettingsGo()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = FamilyBaby });

        // Act
        await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync(
            $"SELECT count(*) FROM care_entries WHERE id = '{entry.Id.Value}' AND household_id = '{Account.Value}' AND subject_baby_id = '{FamilyBaby.Value}' AND logged_by = 'user:{User.Value}';"))
            .Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE id = '{TestDatabase.Baby.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id IN ('{TestDatabase.Baby.Value}', '{TestDatabase.Household.Value}');")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'CareEntry' AND entity_id = '{entry.Id.Value}';")).Should().Be(1, "the push sends the moved state");
        (await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
    }

    [Fact]
    public async Task BringOverAsync_AddAsANewBaby_MovesTheProfileWithItsOutboxRow()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE id = '{TestDatabase.Baby.Value}' AND household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'BabyProfile' AND entity_id = '{TestDatabase.Baby.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task BringOverAsync_AFailureHalfWay_ChangesNothing()
    {
        // Arrange: the last step (dropping the provisional settings) fails.
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await db.ExecuteAsync(
            $"INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('CareEntry', '{entry.Id.Value}', '{TestDatabase.Household.Value}', 'x', 1);");
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE DELETE ON household_settings BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = FamilyBaby });

        // Act
        var act = async () => await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task FreshBootstrapAsync_PendingChanges_RefusesAndKeepsEverything()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var before = await SnapshotAsync(db);

        // Act
        var done = await new HouseholdMoves(db.Database, db.Time).FreshBootstrapAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task FreshBootstrapAsync_EmptyOutbox_ClearsTheRecordsKeepsTheSettingsAndStartsFromZero()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync("DELETE FROM outbox;");
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq) VALUES ('{TestDatabase.Household.Value}', 42);");

        // Act
        var done = await new HouseholdMoves(db.Database, db.Time).FreshBootstrapAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        done.Should().BeTrue();
        (await db.ScalarAsync("SELECT (SELECT count(*) FROM care_entries) + (SELECT count(*) FROM baby_profiles) + (SELECT count(*) FROM duplicate_dismissals);")).Should().Be(0);
        (await db.ScalarAsync("SELECT count(*) FROM household_settings;")).Should().Be(1);
        (await new SyncStore(db.Database).GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Should().Be(new SyncCursor(0, null));
    }

    [Fact]
    public async Task DeleteHouseholdAsync_ARemovedHousehold_GoesWithItsOutboxAndLeavesTheOtherHousehold()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);

        // Act
        await new HouseholdMoves(db.Database, db.Time).DeleteHouseholdAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task SyncState_ConsentAndSoftLock_RoundTripPerAccountAndHousehold()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var state = new SyncState(db.Database);

        // Act
        await state.SetConsentAsync(User, ConsentState.Given, CancellationToken.None);
        await state.SetSoftLockAsync(Account, true, CancellationToken.None);

        // Assert
        (await state.GetConsentAsync(User, CancellationToken.None)).Should().Be(ConsentState.Given);
        (await state.GetConsentAsync(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2")), CancellationToken.None)).Should().Be(ConsentState.Unknown);
        (await state.IsSoftLockedAsync(Account, CancellationToken.None)).Should().BeTrue();
        await state.SetSoftLockAsync(Account, false, CancellationToken.None);
        (await state.IsSoftLockedAsync(Account, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task MoveAllAsync_CursorsAndRejections_CursorsGoAndRejectionsFollowTheirRecords()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{Account.Value}', 7, NULL);");
        await db.ExecuteAsync(
            $"INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('CareEntry', '{entry.Id.Value}', '{TestDatabase.Household.Value}', 'x', 1);");

        // Act
        await new HouseholdMoves(db.Database, db.Time).MoveAllAsync(TestDatabase.Household, Account, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM sync_cursors;")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM sync_rejections WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await new SyncStore(db.Database).GetCursorAsync(Account, CancellationToken.None)).Should().Be(new SyncCursor(0, 0), "the phone holds records now");
    }

    [Fact]
    public async Task BringOverAsync_CursorsAndRejections_CursorsGoAndTheDroppedRecordsRejectionsGoWithThem()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{Account.Value}', 7, NULL);");
        await db.ExecuteAsync(
            $"""
            INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES
            ('BabyProfile', '{TestDatabase.Baby.Value}', '{TestDatabase.Household.Value}', 'x', 1),
            ('CareEntry', '{entry.Id.Value}', '{TestDatabase.Household.Value}', 'x', 1);
            """);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = FamilyBaby });

        // Act
        await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM sync_cursors;")).Should().Be(0);
        (await db.ScalarAsync("SELECT count(*) FROM sync_rejections WHERE entity_type = 'BabyProfile';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM sync_rejections WHERE entity_type = 'CareEntry' AND household_id = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task DeleteHouseholdAsync_CursorsRejectionsAndState_GoForThatHouseholdOnly()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await db.ExecuteAsync(
            $"""
            INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{TestDatabase.Household.Value}', 5, NULL), ('{Account.Value}', 9, NULL);
            INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('CareEntry', '{entry.Id.Value}', '{TestDatabase.Household.Value}', 'x', 1);
            """);

        // Act
        await new HouseholdMoves(db.Database, db.Time).DeleteHouseholdAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM sync_rejections;")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM sync_cursors WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM sync_cursors WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
    }

    [Fact]
    public async Task BringOverAsync_SourceHasACursorRow_MovesNothingAndActivatesTheTarget()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{TestDatabase.Household.Value}', 3, NULL);");
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = FamilyBaby });

        // Act
        var done = await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE id = '{entry.Id.Value}' AND household_id = '{TestDatabase.Household.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE key = 'active_household' AND value = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task BringOverAsync_SourceHoldsAPulledRecord_MovesNothing()
    {
        // Arrange: a record with no outbox row and no rejection came from the server.
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await db.ExecuteAsync($"DELETE FROM outbox WHERE entity_type = 'CareEntry' AND entity_id = '{entry.Id.Value}';");
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        var done = await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE id = '{TestDatabase.Baby.Value}' AND household_id = '{TestDatabase.Household.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task MoveAllAsync_SourceHasSynced_MovesNothingAndActivatesTheTarget()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{TestDatabase.Household.Value}', 3, NULL);");

        // Act
        var done = await new HouseholdMoves(db.Database, db.Time).MoveAllAsync(TestDatabase.Household, Account, User, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE key = 'active_household' AND value = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task HasEverSyncedAsync_RejectedRecordsAndPendingOnes_AreNotSynced()
    {
        // Arrange: PhoneAsync's records all wait in the outbox; a rejected one has no outbox row.
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var moves = new HouseholdMoves(db.Database, db.Time);
        var pending = await moves.HasEverSyncedAsync(TestDatabase.Household, CancellationToken.None);
        await db.ExecuteAsync("DELETE FROM outbox;");
        await db.ExecuteAsync(
            $"""
            INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES
            ('CareEntry', '{entry.Id.Value}', '{TestDatabase.Household.Value}', 'x', 1),
            ('BabyProfile', '{TestDatabase.Baby.Value}', '{TestDatabase.Household.Value}', 'x', 1);
            """);
        var dismissal = await db.ScalarAsync("SELECT count(*) FROM duplicate_dismissals;");
        await db.ExecuteAsync("DELETE FROM duplicate_dismissals;");

        // Act
        var rejected = await moves.HasEverSyncedAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        dismissal.Should().Be(1);
        pending.Should().BeFalse();
        rejected.Should().BeFalse();
    }

    [Fact]
    public async Task BringOverAsync_ABabyTheMappingDoesNotName_MovesWithTheRest()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?>());

        // Act
        await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE id = '{TestDatabase.Baby.Value}' AND household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
    }

    [Fact]
    public async Task BringOverAsync_AddAsNew_EntriesKeepTheirBaby()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE id = '{entry.Id.Value}' AND subject_baby_id = '{TestDatabase.Baby.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task FreshBootstrapAsync_ARejectionExists_RefusesAndKeepsEverything()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync("DELETE FROM outbox;");
        await db.ExecuteAsync(
            $"INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('CareEntry', '{entry.Id.Value}', '{TestDatabase.Household.Value}', 'x', 1);");
        var before = await SnapshotAsync(db);

        // Act
        var done = await new HouseholdMoves(db.Database, db.Time).FreshBootstrapAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task DeleteHouseholdAsync_TheOtherHouseholdsOutbox_Survives()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await db.ExecuteAsync(
            $"INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device) VALUES ('{Guid.CreateVersion7(db.Time.GetUtcNow())}', 'BabyProfile', '{FamilyBaby.Value}', 'upsert', 1, 0, '{TestDatabase.Baby.Value}');");

        await db.ExecuteAsync(
            $"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min, logged_by, source, details, version_wall_ms, version_counter, version_device)
            SELECT '{Guid.CreateVersion7(db.Time.GetUtcNow())}', '{Account.Value}', kind, '{FamilyBaby.Value}', is_session, start_utc_ms, start_offset_min, logged_by, source, details, version_wall_ms, version_counter, version_device
            FROM care_entries LIMIT 1;
            """);

        // Act
        await new HouseholdMoves(db.Database, db.Time).DeleteHouseholdAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id = '{FamilyBaby.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task HasEverSyncedAsync_APulledBabyProfile_IsSynced()
    {
        // Arrange: only the baby profile lost its outbox row, as a pulled one has none.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync("DELETE FROM outbox WHERE entity_type = 'BabyProfile';");

        // Act
        var synced = await new HouseholdMoves(db.Database, db.Time).HasEverSyncedAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        synced.Should().BeTrue();
    }

    [Fact]
    public async Task HasEverSyncedAsync_APulledDismissal_IsSynced()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync("DELETE FROM outbox WHERE entity_type = 'DuplicateDismissal';");

        // Act
        var synced = await new HouseholdMoves(db.Database, db.Time).HasEverSyncedAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        synced.Should().BeTrue();
    }

    [Fact]
    public async Task BringOverAsync_APlanKeyedByTheFamilysOwnBaby_NeverTouchesThatBaby()
    {
        // Arrange: the plan names a baby that lives in the target household, not in the source.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [FamilyBaby] = TestDatabase.Baby });

        // Act
        await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE id = '{FamilyBaby.Value}' AND household_id = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task ActivateAsync_AHousehold_OnlyNamesItActive()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var outboxBefore = await db.ScalarAsync("SELECT count(*) FROM outbox;");

        // Act
        await new HouseholdMoves(db.Database, db.Time).ActivateAsync(Account, CancellationToken.None);

        // Assert
        (await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(outboxBefore);
    }
    // Final review C1 and I1: every test below starts with none of the family's settings on the phone, and asserts through
    // what the app reads (ActiveHouseholdAsync, ListHouseholdsAsync), not only the app_state row.

    [Fact]
    public async Task BringOverAsync_TheFamilysSettingsAreNotOnThePhone_TheSourceSettingsBecomeTheAccountHouseholdsAndItIsActive()
    {
        // Arrange: consent "Not now" pulled nothing, or the family never pushed settings: only this phone's settings exist.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var moves = new HouseholdMoves(db.Database, db.Time);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        await moves.BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await moves.ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
        var households = await db.Households.ListHouseholdsAsync(CancellationToken.None);
        households.Should().ContainSingle();
        households[0].Id.Should().Be(Account);
        households[0].IsDeleted.Should().BeFalse();
        households[0].TimeZoneId.Should().Be("Europe/Amsterdam");
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{Account.Value}';")).Should().Be(1, "the settings upload to the family");
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
    }

    [Fact]
    public async Task ActiveHouseholdAsync_TheNamedHouseholdHasNoSettings_ReadsTheFirstLiveOneAndKeepsTheName()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"INSERT INTO app_state (key, value) VALUES ('active_household', '{Account.Value}');");

        // Act
        var active = await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None);

        // Assert
        active.Should().Be(TestDatabase.Household);
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE key = 'active_household' AND value = '{Account.Value}';"))
            .Should().Be(1, "a fallback read never replaces the household the phone named");
    }

    [Fact]
    public async Task MoveAllAsync_SourceHasSyncedAndTheTargetHasNoSettings_TheTargetGetsACopyOfTheSettingsAndIsActive()
    {
        // Arrange: account A's household synced; account B signs in with `created`, so the move is refused.
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{TestDatabase.Household.Value}', 3, NULL);");
        var moves = new HouseholdMoves(db.Database, db.Time);

        // Act
        var moved = await moves.MoveAllAsync(TestDatabase.Household, Account, User, CancellationToken.None);

        // Assert
        moved.Should().BeFalse();
        (await moves.ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
        var households = await db.Households.ListHouseholdsAsync(CancellationToken.None);
        households.Select(h => h.Id).Should().Equal(Account, TestDatabase.Household);
        households[0].TimeZoneId.Should().Be("Europe/Amsterdam");
        households[0].VolumeUnit.Should().Be(VolumeUnit.Millilitres);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{Account.Value}';")).Should().Be(1);
        (await db.Households.ListBabiesAsync(Account, CancellationToken.None)).Should().BeEmpty("babies are never copied");
        (await db.Households.ListBabiesAsync(TestDatabase.Household, CancellationToken.None)).Should().ContainSingle();
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE id = '{entry.Id.Value}' AND household_id = '{TestDatabase.Household.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task BringOverAsync_SourceHasSyncedAndTheTargetHasNoSettings_TheTargetGetsACopyOfTheSettingsAndIsActive()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{TestDatabase.Household.Value}', 3, NULL);");
        var moves = new HouseholdMoves(db.Database, db.Time);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        var moved = await moves.BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        moved.Should().BeFalse();
        (await moves.ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
        (await db.Households.ListHouseholdsAsync(CancellationToken.None)).Select(h => h.Id).Should().Equal(Account, TestDatabase.Household);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(1, "the source is untouched");
    }

    [Fact]
    public async Task ActivateAsync_TheTargetHasNoSettings_CopiesTheActiveHouseholdsSettingsWithAnOutboxRow()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var moves = new HouseholdMoves(db.Database, db.Time);

        // Act
        await moves.ActivateAsync(Account, CancellationToken.None);

        // Assert
        (await moves.ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
        var households = await db.Households.ListHouseholdsAsync(CancellationToken.None);
        households.Select(h => h.Id).Should().Equal(Account, TestDatabase.Household);
        households[0].TimeZoneId.Should().Be("Europe/Amsterdam");
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task BringOverAsync_APlanKeyedByTheFamilysOwnBaby_KeepsThatBabysOutboxRowAndRejection()
    {
        // Arrange: the family's baby has a pending change and a rejection; the plan wrongly names it as a local baby.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await db.ExecuteAsync(
            $"""
            INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
            VALUES ('{Guid.CreateVersion7(db.Time.GetUtcNow())}', 'BabyProfile', '{FamilyBaby.Value}', 'upsert', 1, 0, '{TestDatabase.Baby.Value}');
            INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('BabyProfile', '{FamilyBaby.Value}', '{Account.Value}', 'x', 1);
            """);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [FamilyBaby] = TestDatabase.Baby });

        // Act
        await new HouseholdMoves(db.Database, db.Time).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'BabyProfile' AND entity_id = '{FamilyBaby.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM sync_rejections WHERE entity_type = 'BabyProfile' AND entity_id = '{FamilyBaby.Value}';")).Should().Be(1);
    }
}
