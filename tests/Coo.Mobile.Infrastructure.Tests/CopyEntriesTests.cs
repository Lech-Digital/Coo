using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static Coo.Mobile.Infrastructure.Tests.HouseholdMovesTests;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>
/// Spec 011b §6.2 and §8.4 on a real SQLite file: the copy is one transaction, makes new records, keeps the source, and runs
/// once per pair; the last family is replaced in the same transaction that deletes it.
/// </summary>
public sealed class CopyEntriesTests
{
    private static readonly HlcStamp First = new(1_790_000_000_000, 10, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d9")));

    /// <summary>The phone's household (synced: it has a cursor) with two diapers and a dismissal pairing them; the family pulled beside it.</summary>
    private static async Task<(TestDatabase Db, CareEntry First, CareEntry Second)> SyncedPhoneAsync()
    {
        var (db, first) = await PhoneAsync();
        var second = CareEntry.Create(await db.NewIdAsync(), TestDatabase.Household, EntrySubject.Baby(TestDatabase.Baby),
            new Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Dirty), EntryTiming.Point(db.Time.GetLocalNow().AddMinutes(3)),
            PersonRef.User(User), EntrySource.Manual, await db.Device.NextAsync(CancellationToken.None), db.Time.GetUtcNow()).Value;
        await db.Store.SaveAsync(second, CancellationToken.None);
        await new DuplicateDismissalStore(db.Database, db.Time, db.Writes).SaveAsync(DuplicateDismissal.Create(
            new DismissalId(Guid.CreateVersion7(db.Time.GetUtcNow().AddSeconds(1))), TestDatabase.Household, new EntryPair(first.Id, second.Id),
            await db.Device.NextAsync(CancellationToken.None)), CancellationToken.None);
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{TestDatabase.Household.Value}', 7, NULL); DELETE FROM outbox;");
        await PullFamilyAsync(db);
        return (db, first, second);
    }

    private static BringOverPlan ToFamily(BabyId? target = null) =>
        new(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = target ?? FamilyBaby });

    /// <summary>Every stored row that names the source, except the two keys a copy writes on purpose (the active family, the marker).</summary>
    private static async Task<string> SourceRowsAsync(TestDatabase db)
    {
        var all = await SnapshotAsync(db);
        return string.Join('\n', all.Split('\n').Where(l => l.Contains(TestDatabase.Household.Value.ToString(), StringComparison.Ordinal)
            && !l.StartsWith("active_household", StringComparison.Ordinal) && !l.StartsWith("copied_from", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CopyAsync_SyncedSource_MakesNewRecordsInTheTargetAndLeavesTheSourceUntouched()
    {
        // Arrange
        var (db, first, second) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var sourceBefore = await SourceRowsAsync(db);

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(2, 0));
        (await SourceRowsAsync(db)).Should().Be(sourceBefore, "the source is not touched: same records, outbox and cursor");
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND subject_baby_id = '{FamilyBaby.Value}' AND id NOT IN ('{first.Id.Value}', '{second.Id.Value}');"))
            .Should().Be(2);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND logged_by = 'this-device';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT min(version_counter) * 100 + max(version_counter) FROM care_entries WHERE household_id = '{Account.Value}';"))
            .Should().Be(10 * 100 + 11, "the copies take consecutive counters from the reserved stamp (ruling 8)");
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(3, "two entries and a dismissal; the target's settings and the mapped baby need none");
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE key = 'copied_from:{TestDatabase.Household.Value}:{Account.Value}';")).Should().Be(1);
        (await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
    }

    [Fact]
    public async Task CopyAsync_DetailsWithAnUnknownProperty_KeepsThemByteForByte()
    {
        // Arrange: ADR 0010 §3, nothing unknown is lost.
        var (db, first, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        const string details = """{"v":1,"contents":"wet","futureField":{"a":[1,2]}}""";
        await db.ExecuteAsync($"UPDATE care_entries SET details = '{details}' WHERE id = '{first.Id.Value}';");

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        await using var connection = await db.OpenAsync();
        (await connection.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND details = '{details}';", CancellationToken.None))
            .Should().Be(1L);
    }

    [Fact]
    public async Task CopyAsync_TombstoneAndAnUnknownKind_AreNotCopiedAndOnlyTheLiveOneIsCounted()
    {
        // Arrange: ruling 9.
        var (db, first, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE care_entries SET is_deleted = 1 WHERE id = '{first.Id.Value}';");
        await db.ExecuteAsync(
            $$"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min, logged_by, source, details,
                version_wall_ms, version_counter, version_device, integrity, integrity_reason)
            VALUES ('{{Guid.CreateVersion7(db.Time.GetUtcNow().AddHours(1))}}', '{{TestDatabase.Household.Value}}', 'potty', '{{TestDatabase.Baby.Value}}', 0,
                1790000000000, 120, 'user:{{User.Value}}', 'manual', '{"v":1}', 1, 0, '{{TestDatabase.Baby.Value}}', 'read-only', 'entry.kind-unknown');
            """);

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(1, 1));
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(0, "only one of its entries was copied");
    }

    [Fact]
    public async Task CopyAsync_BabyAddedAsNew_CreatesANewProfileWithItsOwnIdAndOutboxRow()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(plan, User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}' AND name = 'Maja' AND id <> '{TestDatabase.Baby.Value}';"))
            .Should().Be(2, "the family's own Maja and the new one");
        (await db.ScalarAsync("SELECT count(*) FROM outbox WHERE entity_type = 'BabyProfile';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND subject_baby_id = '{FamilyBaby.Value}';")).Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_SecondTimeForThePair_CopiesNothing()
    {
        // Arrange: Review Focus 3.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);
        await records.CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);
        var after = await SnapshotAsync(db);

        // Act
        var again = await records.CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        again.Should().BeNull();
        (await SnapshotAsync(db)).Should().Be(after);
    }

    [Fact]
    public async Task CopyAsync_FailurePartWay_ChangesNothing()
    {
        // Arrange: the dismissal is written last; failing there must undo the entries already copied.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE INSERT ON duplicate_dismissals BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);

        // Act
        var act = async () => await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task DeleteAndReplaceAsync_TheLastFamily_LeavesOnlyTheNewOneActiveWithItsOutboxRow()
    {
        // Arrange: spec §8.4.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var replacement = HouseholdSettings.Create(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000af")), VolumeUnit.Ounces, "Europe/Warsaw",
            await db.Device.NextAsync(CancellationToken.None)).Value;

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).DeleteAndReplaceAsync(TestDatabase.Household, replacement, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM household_settings;")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{replacement.Id.Value}';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(1);
        (await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(replacement.Id);
    }

    [Fact]
    public async Task DeleteAndReplaceAsync_TheLastFamily_NamesTheReplacementAsTheOnlyFamilyTheServerMayCreate()
    {
        // Arrange: final review C3, written in the same transaction.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var replacement = HouseholdSettings.Create(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000af")), VolumeUnit.Ounces, "Europe/Warsaw",
            await db.Device.NextAsync(CancellationToken.None)).Value;
        var state = new SyncState(db.Database);

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).DeleteAndReplaceAsync(TestDatabase.Household, replacement, CancellationToken.None);
        var named = await state.GetReplacementHouseholdAsync(CancellationToken.None);
        await state.ClearReplacementHouseholdAsync(TestDatabase.Household, CancellationToken.None);
        var afterClearingAnother = await state.GetReplacementHouseholdAsync(CancellationToken.None);
        await state.ClearReplacementHouseholdAsync(replacement.Id, CancellationToken.None);

        // Assert
        named.Should().Be(replacement.Id);
        afterClearingAnother.Should().Be(replacement.Id);
        (await state.GetReplacementHouseholdAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAndReplaceAsync_FailurePartWay_ChangesNothing()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE INSERT ON household_settings BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);
        var replacement = HouseholdSettings.Create(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000af")), VolumeUnit.Ounces, "Europe/Warsaw",
            await db.Device.NextAsync(CancellationToken.None)).Value;

        // Act
        var act = async () => await new FamilyRecords(db.Database, db.Time, db.Writes).DeleteAndReplaceAsync(TestDatabase.Household, replacement, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task ListUnsharedEntriesAsync_OutboxHoldsOneEntry_ListsItOldestFirstAndPendingCountsIt()
    {
        // Arrange: the removal sheet's "1 entry you logged wasn't shared yet" (spec §8.2).
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);

        // Act
        var unshared = await records.ListUnsharedEntriesAsync(TestDatabase.Household, CancellationToken.None);
        var pending = await records.CountPendingAsync([TestDatabase.Household, Account], CancellationToken.None);
        var count = await records.CountRecordsAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        unshared.Should().ContainSingle().Which.Id.Should().Be(entry.Id);
        pending.Should().Be(4, "the settings, the baby, the entry and the dismissal wait in the outbox");
        count.Should().Be(3, "an entry, a baby and a dismissal");
    }

    [Fact]
    public async Task CopyAsync_MappedBabyIsNotInTheTarget_BecomesANewProfileInstead()
    {
        // Arrange: the plan names a baby the target does not hold; its entries must not point at a stranger.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var stranger = new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b8"));

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(stranger), User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND subject_baby_id = '{stranger.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}' AND id <> '{FamilyBaby.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries e JOIN baby_profiles b ON b.id = e.subject_baby_id WHERE e.household_id = '{Account.Value}' AND b.household_id = '{Account.Value}';"))
            .Should().Be(2);
    }

    [Fact]
    public async Task CopyAsync_MappedBabyIsADeletedProfileInTheTarget_BecomesANewProfileInstead()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE baby_profiles SET is_deleted = 1 WHERE id = '{FamilyBaby.Value}';");

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND subject_baby_id = '{FamilyBaby.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}' AND is_deleted = 0;")).Should().Be(1);
    }

    [Fact]
    public async Task CopyAsync_EntryOfADeletedBaby_IsSkippedAndCounted()
    {
        // Arrange: the baby's profile is a tombstone, so it is not in the baby map.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE baby_profiles SET is_deleted = 1 WHERE id = '{TestDatabase.Baby.Value}';");

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(0, 2));
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_ThisDevicesPerson_BecomesTheSignedInUserInBothFields()
    {
        // Arrange
        var (db, first, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE care_entries SET subject_baby_id = NULL, subject_person = 'this-device' WHERE id = '{first.Id.Value}';");

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND subject_person = 'user:{User.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND (subject_person = 'this-device' OR logged_by = 'this-device');")).Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_DismissalWhoseCopiesSortTheOtherWay_StoresThePairInOrder()
    {
        // Arrange: the later entry is copied first, so the copies' IDs sort opposite to the originals'.
        var (db, first, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE care_entries SET start_utc_ms = start_utc_ms + 3600000 WHERE id = '{first.Id.Value}';");

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}' AND entry_a < entry_b;")).Should().Be(1);
    }

    [Fact]
    public async Task CopyAsync_DeletedDismissal_IsNotCopied()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE duplicate_dismissals SET is_deleted = 1 WHERE household_id = '{TestDatabase.Household.Value}';");

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_StampsStartAtTheReservedCounterAcrossBabiesEntriesAndDismissals()
    {
        // Arrange: a new baby (1), two entries (2) and a dismissal (1) use counters 10..13.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(plan, User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT min(version_counter) * 100 + max(version_counter) FROM outbox;")).Should().Be(10 * 100 + 13);
        (await db.ScalarAsync("SELECT count(DISTINCT version_counter) FROM outbox;")).Should().Be(4);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE version_wall_ms = {First.WallClockMs} AND version_device = '{First.DeviceId.Value}';")).Should().Be(4);
    }

    [Fact]
    public async Task CopyAsync_TargetHoldsEntriesAlready_LeavesThemAndTheirOutboxAlone()
    {
        // Arrange: only the source's records are read; the target's own entry stays as it was.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var own = Guid.CreateVersion7(db.Time.GetUtcNow().AddDays(1));
        await db.ExecuteAsync(
            $$"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min, logged_by, source, details,
                version_wall_ms, version_counter, version_device)
            VALUES ('{{own}}', '{{Account.Value}}', 'diaper', '{{FamilyBaby.Value}}', 0, 1790000000000, 120, 'user:{{User.Value}}', 'manual', '{"v":1,"contents":"wet"}', 1, 0, '{{FamilyBaby.Value}}');
            """);

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(2, 0));
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE id = '{own}' AND version_counter = 0;")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id = '{own}';")).Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_MarkerForAnotherPair_DoesNotBlockThisOne()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"INSERT INTO app_state (key, value) VALUES ('copied_from:{Account.Value}:{TestDatabase.Household.Value}', '1');");

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(2, 0));
    }

    [Fact]
    public async Task DeleteAndReplaceAsync_ReplacementWithHiddenKinds_StoresThemSortedAndNamesTheNewFamilyActive()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var replacement = HouseholdSettings.Create(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000af")), VolumeUnit.Ounces, "Europe/Warsaw",
            await db.Device.NextAsync(CancellationToken.None)).Value;
        replacement.SetHiddenKinds([EntryKindCode.From("feed").Value, EntryKindCode.From("diaper").Value], await db.Device.NextAsync(CancellationToken.None));

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).DeleteAndReplaceAsync(TestDatabase.Household, replacement, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{replacement.Id.Value}' AND volume_unit = 'oz' AND hidden_kinds = '[\"diaper\",\"feed\"]';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM app_state WHERE key = 'active_household';")).Should().Be(1);
    }

    [Fact]
    public async Task CountPendingAsync_TwoOutboxRowsForOneRecord_CountsItOnce()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var before = await new FamilyRecords(db.Database, db.Time, db.Writes).CountPendingAsync([TestDatabase.Household], CancellationToken.None);
        await db.ExecuteAsync(
            $"INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device) " +
            $"SELECT 'x', 'CareEntry', '{entry.Id.Value}', 'upsert', 1, 99, '{TestDatabase.Baby.Value}';");

        // Act
        var after = await new FamilyRecords(db.Database, db.Time, db.Writes).CountPendingAsync([TestDatabase.Household], CancellationToken.None);

        // Assert
        after.Should().Be(before);
    }

    [Fact]
    public async Task CountPendingAsync_NoHouseholds_IsZeroAndOtherFamiliesAreNotCounted()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);

        // Act
        var none = await records.CountPendingAsync([], CancellationToken.None);
        var other = await records.CountPendingAsync([Account], CancellationToken.None);

        // Assert
        none.Should().Be(0);
        other.Should().Be(0);
    }

    [Fact]
    public async Task ListUnsharedEntriesAsync_DeletedSentAndOtherFamilyEntries_AreNotListed()
    {
        // Arrange: the entry waits in the outbox; a sent one, a tombstone and another family's must not be listed.
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var sent = await db.DiaperAsync(db.Time.GetLocalNow().AddMinutes(5));
        var gone = await db.DiaperAsync(db.Time.GetLocalNow().AddMinutes(6));
        await db.ExecuteAsync($"DELETE FROM outbox WHERE entity_id = '{sent.Id.Value}';");
        await db.ExecuteAsync($"UPDATE care_entries SET is_deleted = 1 WHERE id = '{gone.Id.Value}';");
        var foreign = await db.DiaperAsync(db.Time.GetLocalNow().AddMinutes(7));
        await db.ExecuteAsync($"UPDATE care_entries SET household_id = '{Account.Value}' WHERE id = '{foreign.Id.Value}';");

        // Act
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);
        var unshared = await records.ListUnsharedEntriesAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        unshared.Select(e => e.Id).Should().Equal(entry.Id);
    }

    [Fact]
    public async Task ListUnsharedEntriesAsync_SeveralEntries_ListsOldestFirst()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var older = await db.DiaperAsync(db.Time.GetLocalNow().AddHours(-2));

        // Act
        var unshared = await new FamilyRecords(db.Database, db.Time, db.Writes).ListUnsharedEntriesAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        unshared.Select(e => e.Id).Should().Equal(older.Id, entry.Id);
    }

    [Fact]
    public async Task CountRecordsAsync_TombstoneAndAnotherFamilysBaby_CountsOnlyThisHouseholdsRecordsTombstonesIncluded()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE care_entries SET is_deleted = 1 WHERE id = '{entry.Id.Value}';");
        await PullFamilyAsync(db);

        // Act
        var count = await new FamilyRecords(db.Database, db.Time, db.Writes).CountRecordsAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        count.Should().Be(3, "the tombstone still counts toward the stamp budget; the family's baby does not");
    }

    [Fact]
    public async Task CopyAsync_BudgetSmallerThanTheStampsNeeded_ThrowsAndWritesNothing()
    {
        // Arrange: two entries, one new baby and a dismissal need 4 stamps; only 3 were reserved (a pull added records after counting).
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });
        var before = await SnapshotAsync(db);

        // Act
        var act = async () => await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(plan, User, First, 3, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<StampBudgetExceededException>();
        (await SnapshotAsync(db)).Should().Be(before, "nothing is written: no records, no marker, no active family");
    }

    [Fact]
    public async Task CopyAsync_BudgetExactlyTheStampsNeeded_Copies()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(plan, User, First, 4, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(2, 0));
    }

    [Fact]
    public async Task CopyAsync_SourceAndTargetTheSame_Throws()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var plan = new BringOverPlan(TestDatabase.Household, TestDatabase.Household, new Dictionary<BabyId, BabyId?>());

        // Act
        var act = async () => await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(plan, User, First, 100, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CopyAsync_SourceHasAnOutboxRowAndARejection_LeavesBothUntouched()
    {
        // Arrange
        var (db, first, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync(
            $"""
            INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
            VALUES ('src-1', 'CareEntry', '{first.Id.Value}', 'upsert', 1, 5, '{TestDatabase.Baby.Value}');
            INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms)
            VALUES ('CareEntry', '{first.Id.Value}', '{TestDatabase.Household.Value}', 'too-old', 1);
            """);
        var outboxBefore = await db.ScalarAsync("SELECT count(*) FROM outbox WHERE entity_id = '" + first.Id.Value + "';");
        var rejectionsBefore = await SnapshotAsync(db);

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM outbox WHERE entity_id = '" + first.Id.Value + "';")).Should().Be(outboxBefore);
        (await db.ScalarAsync($"SELECT count(*) FROM sync_rejections WHERE household_id = '{TestDatabase.Household.Value}' AND reason = 'too-old';")).Should().Be(1);
        rejectionsBefore.Should().Contain("too-old");
    }

    [Fact]
    public async Task CopyAsync_Success_NotifiesOnce()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var notified = 0;
        db.Writes.Written += (_, _) => notified++;

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);

        // Assert
        notified.Should().Be(1);
    }

    [Fact]
    public async Task CopyAsync_AlreadyCopiedOrFailing_DoesNotNotify()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);
        await records.CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);
        var notified = 0;
        db.Writes.Written += (_, _) => notified++;
        var tooSmall = new BringOverPlan(TestDatabase.Household, new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000aa")), new Dictionary<BabyId, BabyId?>());

        // Act
        var again = await records.CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);
        var failing = async () => await records.CopyAsync(tooSmall, User, First, 0, CancellationToken.None);

        // Assert
        again.Should().BeNull();
        await failing.Should().ThrowAsync<StampBudgetExceededException>();
        notified.Should().Be(0);
    }

    [Fact]
    public async Task CountPendingAsync_TwoFamiliesWithPendingRows_AddsThem()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);
        await records.CopyAsync(ToFamily(), User, First, 100, CancellationToken.None);
        var source = await records.CountPendingAsync([TestDatabase.Household], CancellationToken.None);
        var target = await records.CountPendingAsync([Account], CancellationToken.None);

        // Act
        var both = await records.CountPendingAsync([TestDatabase.Household, Account], CancellationToken.None);

        // Assert
        target.Should().Be(3, "the copies of two entries and a dismissal");
        both.Should().Be(source + target);
    }
}
