using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

public sealed class DeviceAndMigrationTests
{
    [Fact]
    public async Task VersionClock_AcrossRestarts_KeepsIncreasing()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var before = await db.Device.NextAsync(CancellationToken.None);

        // Act: a fresh DeviceState is what a restarted app gets; the phone's clock has also jumped back.
        db.Time.Advance(TimeSpan.FromHours(-3));
        using var restarted = new DeviceState(db.Database, db.Time);
        var after = await restarted.NextAsync(CancellationToken.None);

        // Assert
        after.Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task DeviceIdentity_SecondLaunch_ReturnsSameId()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var first = await db.Device.GetAsync(CancellationToken.None);

        // Act
        await db.StartupAsync("1.0.0");
        using var relaunched = new DeviceState(db.Database, db.Time);
        var second = await relaunched.GetAsync(CancellationToken.None);

        // Assert
        second.Should().Be(first);
    }

    [Fact]
    public async Task DataMigrationRunner_PendingMigration_RunsOnceAndRecordsIt()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();
        var migration = new CountingMigration("2026-10-01-test");
        var runner = new DataMigrationRunner([migration], db.Time);

        // Act
        await runner.RunAsync(connection, CancellationToken.None);
        await runner.RunAsync(connection, CancellationToken.None);

        // Assert
        migration.Runs.Should().Be(1);
        (await connection.ScalarAsync("SELECT count(*) FROM data_migrations WHERE id = '2026-10-01-test';", CancellationToken.None)).Should().Be(1L);
    }

    [Fact]
    public async Task DataMigrationRunner_CrashMidway_ResumesNextLaunch()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();
        var migration = new CountingMigration("2026-10-01-crashy") { CrashOnFirstRun = true };
        var runner = new DataMigrationRunner([migration], db.Time);

        // Act
        var firstLaunch = () => runner.RunAsync(connection, CancellationToken.None);
        await firstLaunch.Should().ThrowAsync<InvalidOperationException>();
        await runner.RunAsync(connection, CancellationToken.None);

        // Assert
        migration.Runs.Should().Be(2, "the crashed run was not recorded, so the next launch runs it again");
        (await connection.ScalarAsync("SELECT count(*) FROM data_migrations;", CancellationToken.None)).Should().Be(1L);
    }

    private sealed class CountingMigration(string id) : IDataMigration
    {
        public string Id { get; } = id;

        public bool CrashOnFirstRun { get; init; }

        public int Runs { get; private set; }

        public Task RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            Runs++;
            return CrashOnFirstRun && Runs == 1
                ? throw new InvalidOperationException("Simulated crash")
                : Task.CompletedTask;
        }
    }
}

public sealed class DevicePreferencesTests
{
    [Fact]
    public async Task GetAppearanceAsync_NothingStored_ReturnsDefaults()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var preferences = new DevicePreferences(db.Database);

        // Act
        var appearance = await preferences.GetAppearanceAsync(CancellationToken.None);

        // Assert
        appearance.Should().Be(Coo.Mobile.Application.Preferences.AppearancePreferences.Default);
    }

    [Fact]
    public async Task SetThenGet_CustomAppearance_RoundTrips()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var preferences = new DevicePreferences(db.Database);
        var custom = new Coo.Mobile.Application.Preferences.AppearancePreferences(
            Coo.Mobile.Application.Preferences.AppearanceMode.Night,
            new Coo.Mobile.Application.Preferences.QuietHours(new TimeOnly(21, 30), new TimeOnly(6, 15)));

        // Act
        await preferences.SetAppearanceAsync(custom, CancellationToken.None);
        var read = await new DevicePreferences(db.Database).GetAppearanceAsync(CancellationToken.None);

        // Assert
        read.Should().Be(custom);
    }

    [Fact]
    public async Task GetAppearanceAsync_UnreadableStoredValue_FallsBackToDefault()
    {
        // Arrange: a value from a newer build that this one cannot read.
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ('appearance.mode', 'Sepia');", CancellationToken.None);

        // Act
        var appearance = await new DevicePreferences(db.Database).GetAppearanceAsync(CancellationToken.None);

        // Assert
        appearance.Mode.Should().Be(Coo.Mobile.Application.Preferences.AppearanceMode.Auto);
    }

    [Fact]
    public async Task GetLanguageAsync_NothingStored_ReturnsSystem()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var language = await new DevicePreferences(db.Database).GetLanguageAsync(CancellationToken.None);

        // Assert
        language.Should().Be(Coo.Mobile.Application.Preferences.LanguagePreference.System);
    }

    [Fact]
    public async Task SetThenGet_Polish_RoundTrips()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var polish = new Coo.Mobile.Application.Preferences.LanguagePreference("pl");

        // Act
        await new DevicePreferences(db.Database).SetLanguageAsync(polish, CancellationToken.None);
        var read = await new DevicePreferences(db.Database).GetLanguageAsync(CancellationToken.None);

        // Assert
        read.Should().Be(polish);
    }

    [Fact]
    public async Task GetLanguageAsync_UnreadableValue_ReturnsSystem()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ('language', 'Klingon!');", CancellationToken.None);

        // Act
        var language = await new DevicePreferences(db.Database).GetLanguageAsync(CancellationToken.None);

        // Assert
        language.IsSystem.Should().BeTrue();
    }

    [Fact]
    public async Task SetThenGet_DayOverride_RoundTrips()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var withOverride = Coo.Mobile.Application.Preferences.AppearancePreferences.Default with
        {
            Override = new Coo.Mobile.Application.Preferences.AppearanceOverride(
                Coo.Mobile.Application.Preferences.AppearanceMode.Day, new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(2))),
        };

        // Act
        await new DevicePreferences(db.Database).SetAppearanceAsync(withOverride, CancellationToken.None);
        var read = await new DevicePreferences(db.Database).GetAppearanceAsync(CancellationToken.None);

        // Assert
        read.Override!.Palette.Should().Be(withOverride.Override.Palette);
        read.Override.Until.Should().Be(withOverride.Override.Until);
    }

    [Fact]
    public async Task SetThenGet_OverrideCleared_ReadsNone()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var preferences = new DevicePreferences(db.Database);
        var withOverride = Coo.Mobile.Application.Preferences.AppearancePreferences.Default with
        {
            Override = new Coo.Mobile.Application.Preferences.AppearanceOverride(
                Coo.Mobile.Application.Preferences.AppearanceMode.Night, new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.FromHours(2))),
        };
        await preferences.SetAppearanceAsync(withOverride, CancellationToken.None);

        // Act
        await preferences.SetAppearanceAsync(withOverride with { Override = null }, CancellationToken.None);
        var read = await preferences.GetAppearanceAsync(CancellationToken.None);

        // Assert
        read.Override.Should().BeNull();
    }
}

public sealed class ExpectedBabyMigrationTests
{
    [Fact]
    public async Task Migrate_0001DatabaseWithABaby_KeepsItThrough0002()
    {
        // Arrange: a database at version 1 with a born baby, as on the owner's phone before this release.
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                DROP TABLE family_memberships;
                DROP TABLE sync_rejections;
                DROP TABLE sync_cursors;
                DROP TABLE duplicate_dismissals;
                DROP TABLE baby_profiles;
                CREATE TABLE baby_profiles (id TEXT NOT NULL PRIMARY KEY, household_id TEXT NOT NULL, name TEXT NOT NULL,
                    nicknames TEXT NOT NULL, birth_date TEXT NOT NULL, due_date TEXT NULL, version_wall_ms INTEGER NOT NULL,
                    version_counter INTEGER NOT NULL, version_device TEXT NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0) STRICT;
                INSERT INTO baby_profiles VALUES ('7a1c0000-0000-7000-8000-0000000000b1', '7a1c0000-0000-7000-8000-0000000000a1',
                    'Test baby', '[]', '2026-08-02', NULL, 1, 0, '7a1c0000-0000-7000-8000-0000000000d1', 0);
                PRAGMA user_version = 1;
                """,
                CancellationToken.None);
        }

        // Act
        await db.StartupAsync("1.0.1");
        var babies = await db.Households.ListBabiesAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("PRAGMA user_version;")).Should().Be(Persistence.SchemaMigrator.LatestVersion);
        babies.Should().ContainSingle().Which.BirthDate.Should().Be(new DateOnly(2026, 8, 2));
    }

    [Fact]
    public async Task SaveThenList_ExpectedBaby_RoundTripsWithoutBirthDate()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var today = new DateOnly(2026, 10, 1);
        var baby = Coo.Domain.Households.BabyProfile.Create(
            TestDatabase.Baby, TestDatabase.Household, "Baby", [], null, today.AddDays(40),
            await db.Device.NextAsync(CancellationToken.None), today).Value;

        // Act
        await db.Households.SaveBabyAsync(baby, CancellationToken.None);
        var listed = await db.Households.ListBabiesAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        listed.Should().ContainSingle().Which.IsBorn.Should().BeFalse();
    }

    [Fact]
    public async Task Insert_BabyWithNeitherDate_IsRefusedByDatabase()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();

        // Act
        var act = () => connection.ExecuteAsync(
            "INSERT INTO baby_profiles (id, household_id, name, nicknames, version_wall_ms, version_counter, version_device) VALUES ('x', 'h', 'B', '[]', 1, 0, 'd');",
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }
}

public sealed class DismissalAndOutboxTests
{
    [Fact]
    public async Task Migrate_0002DatabaseWithOutboxRows_KeepsThemInOrderThrough0003()
    {
        // Arrange: a database at version 2, with two outbox rows, as on the owner's phone before this release.
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)));
        await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 9, 5, 0, TimeSpan.FromHours(2)));
        await using (var connection = await db.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                DROP TABLE family_memberships;
                DROP TABLE sync_rejections;
                DROP TABLE sync_cursors;
                DROP TABLE duplicate_dismissals;
                CREATE TABLE outbox_old AS SELECT * FROM outbox;
                DROP TABLE outbox;
                CREATE TABLE outbox (seq INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, change_id TEXT NOT NULL UNIQUE,
                    entity_type TEXT NOT NULL CHECK (entity_type IN ('CareEntry', 'BabyProfile', 'HouseholdSettings')),
                    entity_id TEXT NOT NULL, operation TEXT NOT NULL, version_wall_ms INTEGER NOT NULL,
                    version_counter INTEGER NOT NULL, version_device TEXT NOT NULL) STRICT;
                INSERT INTO outbox SELECT * FROM outbox_old;
                DROP TABLE outbox_old;
                ALTER TABLE baby_profiles DROP COLUMN sex;
                PRAGMA user_version = 2;
                """,
                CancellationToken.None);
        }

        var before = await db.ScalarAsync("SELECT count(*) FROM outbox;");

        // Act
        await db.StartupAsync("1.0.2");

        // Assert
        (await db.ScalarAsync("PRAGMA user_version;")).Should().Be(Persistence.SchemaMigrator.LatestVersion);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(2);
        (await db.ScalarAsync("SELECT max(seq) FROM outbox;")).Should().Be(2);
        before.Should().Be(2);
    }

    [Fact]
    public async Task SaveThenList_Dismissal_RoundTripsInEitherOrder()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var a = new Coo.Domain.Common.EntryId(Guid.Parse("00000000-0000-7000-8000-00000000000a"));
        var b = new Coo.Domain.Common.EntryId(Guid.Parse("00000000-0000-7000-8000-00000000000b"));
        var store = new DuplicateDismissalStore(db.Database, db.Time, db.Writes);
        var dismissal = Coo.Domain.CareLog.Duplicates.DuplicateDismissal.Create(
            new Coo.Domain.Common.DismissalId(Guid.Parse("00000000-0000-7000-8000-0000000000dd")), TestDatabase.Household,
            new Coo.Domain.CareLog.Duplicates.EntryPair(b, a), await db.Device.NextAsync(CancellationToken.None));

        // Act
        await store.SaveAsync(dismissal, CancellationToken.None);
        var pairs = await store.ListAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        pairs.Should().Contain(new Coo.Domain.CareLog.Duplicates.EntryPair(a, b));
        (await db.ScalarAsync("SELECT count(*) FROM outbox WHERE entity_type = 'DuplicateDismissal';")).Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_UnknownEntityType_IsRefusedByCode()
    {
        // Arrange: the database CHECK is gone (0003); the closed list in code guards instead.
        await using var db = await TestDatabase.CreateAsync();
        var stamp = await db.Device.NextAsync(CancellationToken.None);
        await using var connection = await db.OpenAsync();
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync();

        // Act
        var act = () => Outbox.AppendAsync(connection, transaction, "Recipe", Guid.Parse("00000000-0000-7000-8000-00000000000a"),
            false, stamp, db.Time, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
