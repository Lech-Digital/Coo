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
