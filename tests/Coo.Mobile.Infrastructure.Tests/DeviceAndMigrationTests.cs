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
