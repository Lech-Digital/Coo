using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>G3 (ADR 0010): the database refuses what the code does not know, whatever code tries.</summary>
[Collection(PostgresTests.Name)]
public sealed class SchemaGuardTests(PostgresServer server)
{
    public static TheoryData<string, string, string> RefusedRows => new()
    {
        // case, kind, details
        { "an unknown kind", "teething", """{"v":1}""" },
        { "an unknown details version", "diaper", """{"v":99,"contents":"wet"}""" },
        { "details that are not an object", "diaper", """[1,2,3]""" },
        { "details without a version", "diaper", """{"contents":"wet"}""" },
    };

    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTablesAndRegistersEveryKindVersion()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var tables = await db.ScalarAsync(
            "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory';");
        var kinds = await db.ScalarAsync("SELECT string_agg(kind || ':' || version, ',' ORDER BY kind, version) FROM entry_kind_versions;");

        // Assert
        ((string)tables!).Split(',').Should().BeEquivalentTo(
            "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens", "AspNetUsers", "applied_changes", "auth_nonces",
            "baby_profiles", "care_entries", "consents", "device_sessions", "duplicate_dismissals", "entry_kind_versions",
            "household_settings", "households", "memberships", "sync_conflicts");
        ((string)kinds!).Split(',').Should().Contain(["diaper:1", "feeding:1", "pump:1", "sleep:1"]);
    }

    [Fact]
    public async Task RegisterKinds_RunTwice_IsIdempotent()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var before = await db.ScalarAsync("SELECT count(*) FROM entry_kind_versions;");

        // Act: what every restart of the API does.
        await db.MigrateAsync();

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM entry_kind_versions;")).Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(RefusedRows))]
    public async Task RawInsert_UnknownToTheCode_IsRefusedByTheDatabase(string @case, string kind, string details)
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);

        // Act
        Func<Task> act = () => db.ExecuteAsync(RawInsert(kind, details));

        // Assert
        await act.Should().ThrowAsync<PostgresException>($"the database itself must refuse {@case}");
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0L);
    }

    [Fact]
    public async Task RawInsert_KnownKindAndVersion_IsAccepted()
    {
        // Arrange: the control for the theory above, so a refusal is never just a broken insert.
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);

        // Act
        var inserted = await db.ExecuteAsync(RawInsert("diaper", """{"v":1,"contents":"wet"}"""));

        // Assert
        inserted.Should().Be(1);
    }

    [Fact]
    public async Task RawInsert_UnknownHousehold_IsRefusedByTheDatabase()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        Func<Task> act = () => db.ExecuteAsync(RawInsert("diaper", """{"v":1,"contents":"wet"}"""));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    private static string RawInsert(string kind, string details) =>
        $"""
        INSERT INTO care_entries (
            id, household_id, kind, subject_baby_id, is_session, start_at, start_offset_min, logged_by, source,
            details, version_wall_ms, version_counter, version_device, is_deleted, seq)
        VALUES (
            '{Guid.Parse("7a1c0000-0000-7000-8000-00000000c0f1")}', '{ServerDatabase.Household.Value}', '{kind}', '{ServerDatabase.Baby.Value}', false,
            '2026-10-01T12:00:00Z', 120, 'this-device', 'manual',
            '{details}'::jsonb, 1, 0, '{ServerDatabase.Device.Value}', false, 1);
        """;
}
