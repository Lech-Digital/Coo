using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011: migration 0004, the active household, the local-write signal, and the clock seeing pulled stamps.</summary>
public sealed class SyncSchemaAndSignalTests
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheSyncTablesAtVersionFour()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var tables = await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('sync_cursors', 'sync_rejections');");

        // Assert
        tables.Should().Be(2);
        (await db.ScalarAsync("PRAGMA user_version;")).Should().Be(Persistence.SchemaMigrator.LatestVersion);
    }

    [Fact]
    public async Task Migrate_0003DatabaseWithData_KeepsEveryRowThrough0004()
    {
        // Arrange: a database at version 3 with entries, a delete, settings, a baby, a dismissal, outbox rows and app
        // state, as on the owner's phone at 0.10.0.
        await using var db = await TestDatabase.CreateAsync();
        var first = await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)));
        await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 9, 5, 0, TimeSpan.FromHours(2)));
        await db.Store.DeleteAsync(first.Id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);
        await db.Households.SaveSettingsAsync(
            HouseholdSettings.Create(TestDatabase.Household, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value,
            CancellationToken.None);
        await db.Households.SaveBabyAsync(
            BabyProfile.Create(TestDatabase.Baby, TestDatabase.Household, "Lena", [], new DateOnly(2026, 8, 20), null,
                await db.Device.NextAsync(CancellationToken.None), new DateOnly(2026, 10, 1)).Value,
            CancellationToken.None);
        await new DuplicateDismissalStore(db.Database, db.Time, db.Writes).SaveAsync(
            DuplicateDismissal.Create(new DismissalId(Guid.CreateVersion7(db.Time.GetUtcNow())), TestDatabase.Household,
                new EntryPair(await db.NewIdAsync(), await db.NewIdAsync()), await db.Device.NextAsync(CancellationToken.None)),
            CancellationToken.None);
        await using (var connection = await db.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO app_state (key, value) VALUES ('appearance', 'dark');
                DROP INDEX ix_outbox_entity;
                DROP TABLE family_memberships;
                DROP TABLE sync_rejections;
                DROP TABLE sync_cursors;
                PRAGMA user_version = 3;
                """,
                CancellationToken.None);
        }

        string[] tables = ["care_entries", "household_settings", "baby_profiles", "duplicate_dismissals", "outbox", "app_state", "device_state"];
        var before = new Dictionary<string, List<string>>();
        foreach (var table in tables)
            before[table] = await DumpAsync(db, table);

        // Act: the same app version, so the integrity scan's marker in app_state stays as it was.
        await db.StartupAsync("1.0.0");

        // Assert
        (await db.ScalarAsync("PRAGMA user_version;")).Should().Be(Persistence.SchemaMigrator.LatestVersion);
        foreach (var table in tables)
            (await DumpAsync(db, table)).Should().Equal(before[table], $"migration 0004 leaves {table} untouched");
        before["outbox"].Should().HaveCount(6);
        (await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ('ix_outbox_entity', 'ix_sync_rejections_household');"))
            .Should().Be(2);
        (await db.ScalarAsync("SELECT count(*) FROM sync_cursors;")).Should().Be(0);
    }

    [Theory]
    [InlineData("INSERT INTO sync_cursors (household_id, last_seq) VALUES ('h', -1);")]
    [InlineData("INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('h', 0, -1);")]
    [InlineData("INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('CareEntry', 'e', 'h', '', 1);")]
    public async Task Insert_InvalidSyncRow_IsRefusedByDatabase(string sql)
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();

        // Act
        var act = () => connection.ExecuteAsync(sql, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task SaveAsync_ACareEntry_SignalsOneLocalWriteAfterItIsStored()
    {
        // Arrange: Microsoft.Data.Sqlite runs its async calls inline, so the count is read inside the handler, while the
        // writer is still raising the signal; the task is awaited afterwards rather than blocked on (PF4).
        await using var db = await TestDatabase.CreateAsync();
        Task<long>? outboxWhenSignalled = null;
        var signals = 0;
        db.Writes.Written += (_, _) =>
        {
            signals++;
            outboxWhenSignalled = db.ScalarAsync("SELECT count(*) FROM outbox;");
        };

        // Act
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Assert
        signals.Should().Be(1);
        outboxWhenSignalled.Should().NotBeNull();
        (await outboxWhenSignalled!).Should().Be(1, "the signal comes after the commit");
    }

    [Fact]
    public async Task SaveAsync_HandlerThrows_TheWriteStillSucceedsAndOtherHandlersRun()
    {
        // Arrange: a sync-trigger failure must never fail a care-log write.
        await using var db = await TestDatabase.CreateAsync();
        var later = 0;
        db.Writes.Written += (_, _) => throw new InvalidOperationException("scheduler broke");
        db.Writes.Written += (_, _) => later++;

        // Act
        var act = () => db.DiaperAsync(db.Time.GetLocalNow());

        // Assert
        await act.Should().NotThrowAsync();
        later.Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_WriteFails_SignalsNothing()
    {
        // Arrange: the outbox insert fails, so nothing commits.
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync(
                "CREATE TRIGGER fail_outbox BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT, 'simulated crash'); END;", CancellationToken.None);
        var signals = 0;
        db.Writes.Written += (_, _) => signals++;

        // Act
        var act = () => db.DiaperAsync(db.Time.GetLocalNow());

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        signals.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_AnEntry_SignalsALocalWrite()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        var signals = 0;
        db.Writes.Written += (_, _) => signals++;

        // Act
        await db.Store.DeleteAsync(entry.Id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);

        // Assert
        signals.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_AlreadyDeleted_SignalsNothing()
    {
        // Arrange: no row changes and no outbox row is appended, so there is nothing to sync.
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        await db.Store.DeleteAsync(entry.Id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);
        var signals = 0;
        db.Writes.Written += (_, _) => signals++;

        // Act
        var deleted = await db.Store.DeleteAsync(entry.Id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);

        // Assert
        deleted.Should().BeFalse();
        signals.Should().Be(0);
    }

    [Fact]
    public async Task SaveSettingsSaveBabyAndSaveDismissal_EachSignalALocalWrite()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var signals = 0;
        db.Writes.Written += (_, _) => signals++;
        var settings = HouseholdSettings.Create(TestDatabase.Household, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value;
        var baby = BabyProfile.Create(TestDatabase.Baby, TestDatabase.Household, "Lena", [], new DateOnly(2026, 8, 20), null,
            await db.Device.NextAsync(CancellationToken.None), new DateOnly(2026, 10, 1)).Value;
        var dismissal = DuplicateDismissal.Create(new DismissalId(Guid.CreateVersion7(db.Time.GetUtcNow())), TestDatabase.Household,
            new EntryPair(await db.NewIdAsync(), await db.NewIdAsync()), await db.Device.NextAsync(CancellationToken.None));

        // Act
        await db.Households.SaveSettingsAsync(settings, CancellationToken.None);
        await db.Households.SaveBabyAsync(baby, CancellationToken.None);
        await new DuplicateDismissalStore(db.Database, db.Time, db.Writes).SaveAsync(dismissal, CancellationToken.None);

        // Assert
        signals.Should().Be(3);
    }

    [Fact]
    public async Task ListHouseholdsAsync_ActiveHouseholdNamed_ComesFirstWhateverItsId()
    {
        // Arrange: the account's household (pulled while join-pending) sorts before this phone's by ID.
        await using var db = await TestDatabase.CreateAsync();
        var mine = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1"));
        var accounts = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-000000000001"));
        foreach (var id in new[] { mine, accounts })
            await db.Households.SaveSettingsAsync(
                HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ('active_household', $v);", CancellationToken.None, null, ("$v", Columns.Id(mine.Value)));

        // Act
        var listed = await db.Households.ListHouseholdsAsync(CancellationToken.None);

        // Assert
        listed.Select(h => h.Id).Should().Equal(mine, accounts);
    }

    [Fact]
    public async Task ListHouseholdsAsync_NoActiveHouseholdNamed_OrdersById()
    {
        // Arrange: a phone upgraded from 0.10.0 has no active_household yet.
        await using var db = await TestDatabase.CreateAsync();
        var high = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1"));
        var low = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-000000000001"));
        foreach (var id in new[] { high, low })
            await db.Households.SaveSettingsAsync(
                HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);

        // Act
        var listed = await db.Households.ListHouseholdsAsync(CancellationToken.None);

        // Assert
        listed.Select(h => h.Id).Should().Equal(low, high);
    }

    [Fact]
    public async Task ObserveAsync_StampFromTheFuture_TheNextLocalStampIsHigherAfterARestart()
    {
        // Arrange: another phone's clock is ten minutes ahead (ADR 0002 §4).
        await using var db = await TestDatabase.CreateAsync();
        var remote = new HlcStamp(db.Time.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds(), 3, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d9")));

        // Act
        await db.Device.ObserveAsync(remote, CancellationToken.None);
        using var restarted = new DeviceState(db.Database, db.Time);
        var next = await restarted.NextAsync(CancellationToken.None);

        // Assert
        next.Should().BeGreaterThan(remote);
    }

    [Fact]
    public async Task ObserveAsync_StampFromThePast_DoesNotMoveTheClockBack()
    {
        // Arrange: this phone already stamped a write; an older pulled stamp must not lower the clock.
        await using var db = await TestDatabase.CreateAsync();
        var mine = await db.Device.NextAsync(CancellationToken.None);
        var old = new HlcStamp(mine.WallClockMs - 60_000, 0, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d9")));

        // Act
        await db.Device.ObserveAsync(old, CancellationToken.None);
        var next = await db.Device.NextAsync(CancellationToken.None);

        // Assert
        next.Should().BeGreaterThan(mine);
    }

    private static async Task<List<string>> DumpAsync(TestDatabase db, string table)
    {
        await using var connection = await db.OpenAsync();
        await using var command = connection.Command($"SELECT * FROM {table} ORDER BY rowid;");
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var rows = new List<string>();
        while (await reader.ReadAsync(CancellationToken.None))
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }
}
