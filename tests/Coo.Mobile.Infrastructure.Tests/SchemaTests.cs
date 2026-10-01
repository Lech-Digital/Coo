using System.Security.Cryptography;
using System.Text;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Migrations and the database guard G3 (Spec 004 §4.1, §7.1).</summary>
public sealed class SchemaTests
{
    private const string ValidRow =
        """
        INSERT INTO care_entries (id, household_id, kind, subject_baby_id, subject_person, is_session, start_utc_ms, start_offset_min,
            end_utc_ms, end_offset_min, logged_by, source, details, version_wall_ms, version_counter, version_device)
        VALUES ('{0}', 'h', 'diaper', {1}, {2}, {3}, 1000, 120, {4}, {5}, 'this-device', 'manual', '{6}', 1, 0, 'd');
        """;

    private static readonly CompositeFormat ValidRowFormat = CompositeFormat.Parse(ValidRow);

    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesSchema()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var version = await db.ScalarAsync("PRAGMA user_version;");
        var tables = await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';");

        // Assert
        version.Should().Be(SchemaMigrator.LatestVersion);
        tables.Should().Be(8);
    }

    [Fact]
    public async Task Migrate_RunTwice_IsANoOp()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var act = () => db.StartupAsync("1.0.0");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Migrate_ReleasedScripts_MatchManifestChecksums()
    {
        // Arrange
        var manifest = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "migrations-manifest.txt"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[1], p => p[0]);

        // Act
        var actual = SchemaMigrator.Scripts.ToDictionary(
            s => s.FileName, s => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s.Sql))));

        // Assert: every released script is unchanged. A new script is added to the manifest when it ships.
        foreach (var (file, hash) in manifest)
            actual.Should().ContainKey(file).WhoseValue.Should().Be(hash, $"{file} is released and must never change");
    }

    [Fact]
    public async Task Open_DatabaseFromNewerBuild_RefusesToWrite()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync($"PRAGMA user_version = {SchemaMigrator.LatestVersion + 1};", CancellationToken.None);

        // Act
        var result = await db.StartupAsync("1.0.0");

        // Assert
        result.Should().Be(Coo.Mobile.Application.CareLog.StartupResult.DatabaseFromNewerApp);
        (await db.ScalarAsync("PRAGMA user_version;")).Should().Be(SchemaMigrator.LatestVersion + 1, "nothing was touched");
    }

    [Fact]
    public async Task Migrate_CrashInsideScript_LeavesPreviousVersion()
    {
        // Arrange: a script that fails half-way, run the way the migrator runs one.
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();
        var before = await SchemaMigrator.GetUserVersionAsync(connection, CancellationToken.None);

        // Act
        var act = async () =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("CREATE TABLE half_done (x INTEGER) STRICT; INSERT INTO nope VALUES (1);", CancellationToken.None, transaction);
            await connection.ExecuteAsync("PRAGMA user_version = 99;", CancellationToken.None, transaction);
            await transaction.CommitAsync();
        };

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SchemaMigrator.GetUserVersionAsync(connection, CancellationToken.None)).Should().Be(before);
        (await connection.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE name = 'half_done';", CancellationToken.None)).Should().Be(0L);
    }

    public static TheoryData<string, string, string, string, string, string, string> RefusedRows => new()
    {
        // id, subject_baby_id, subject_person, is_session, end_utc_ms, end_offset_min, details
        { "not-json", "'b'", "NULL", "0", "NULL", "NULL", "not json" },
        { "array", "'b'", "NULL", "0", "NULL", "NULL", "[1]" },
        { "no-version", "'b'", "NULL", "0", "NULL", "NULL", """{"contents":"wet"}""" },
        { "string-version", "'b'", "NULL", "0", "NULL", "NULL", """{"v":"1"}""" },
        { "zero-version", "'b'", "NULL", "0", "NULL", "NULL", """{"v":0}""" },
        { "two-subjects", "'b'", "'this-device'", "0", "NULL", "NULL", """{"v":1}""" },
        { "no-subject", "NULL", "NULL", "0", "NULL", "NULL", """{"v":1}""" },
        { "end-before-start", "'b'", "NULL", "1", "999", "120", """{"v":1}""" },
        { "end-without-offset", "'b'", "NULL", "1", "2000", "NULL", """{"v":1}""" },
        { "point-with-end", "'b'", "NULL", "0", "2000", "120", """{"v":1}""" },
    };

    [Theory]
    [MemberData(nameof(RefusedRows))]
    public async Task Insert_InvalidRow_IsRefusedByDatabase(string id, string baby, string person, string session, string end, string endOffset, string details)
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, ValidRowFormat, id, baby, person, session, end, endOffset, details.Replace("'", "''", StringComparison.Ordinal));

        // Act
        var act = () => connection.ExecuteAsync(sql, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(19, "SQLITE_CONSTRAINT");
    }

    [Fact]
    public async Task Insert_ValidRowByRawSql_IsAccepted()
    {
        // Arrange: proves the refusal tests above fail for their own reason, not a broken template.
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, ValidRowFormat, "fine", "'b'", "NULL", "0", "NULL", "NULL", """{"v":1}""");

        // Act
        var inserted = await connection.ExecuteAsync(sql, CancellationToken.None);

        // Assert
        inserted.Should().Be(1);
    }
}
