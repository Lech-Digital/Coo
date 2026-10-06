using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011b §10.3: the invitations table and its guards.</summary>
[Collection(PostgresTests.Name)]
public sealed class MembersSchemaTests(PostgresServer server)
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheInvitationsTable()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var columns = (string)(await db.ScalarAsync(
            "SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_name = 'invitations';"))!;
        var indexes = (string)(await db.ScalarAsync(
            "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE tablename = 'invitations';"))!;

        // Assert
        columns.Split(',').Should().BeEquivalentTo(
            "accepted_at", "accepted_by", "code_hash", "created_at", "created_by", "expires_at", "household_id", "id", "revoked_at", "role");
        indexes.Split(',').Should().Contain(["ix_invitations_code_hash", "ix_invitations_household_id"]);
    }

    [Fact]
    public async Task Migrate_FromSyncWithData_KeepsEveryRowAndAddsOnlyTheInvitationsTable()
    {
        // Arrange: production as Increment 11 left it, with an account, a family, a membership and a care entry.
        await using var db = await ServerDatabase.CreateAsync(server, migrate: false);
        await db.MigrateToAsync("20261002174901_Sync");
        await SeedAsync(db);
        await db.ExecuteAsync("INSERT INTO entry_kind_versions (kind, version) VALUES ('diaper', 1) ON CONFLICT DO NOTHING;");
        await db.ExecuteAsync($"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_at, start_offset_min, logged_by, source,
                details, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{db.NewId()}', '{ServerDatabase.Household.Value}', 'diaper', '{ServerDatabase.Baby.Value}', false, now(), 120,
                'user:{Owner}', 'manual', '{"{"}"v":1,"contents":"wet"{"}"}', 1, 0, '{ServerDatabase.Device.Value}', false, 1);
            """);

        // Act
        await db.MigrateAsync();

        // Assert
        (await db.ScalarAsync("SELECT (SELECT count(*) FROM care_entries) + (SELECT count(*) FROM memberships) + (SELECT count(*) FROM households) + (SELECT count(*) FROM \"AspNetUsers\");"))
            .Should().Be(4L);
        (await db.ScalarAsync("SELECT count(*) FROM invitations;")).Should().Be(0L);
    }

    [Fact]
    public async Task Insert_SameCodeHashTwice_IsRefused()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await SeedAsync(db);
        await db.ExecuteAsync(Insert(db.NewId(), "h-1"));

        // Act
        var act = async () => await db.ExecuteAsync(Insert(db.NewId(), "h-1"));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Insert_OwnerRole_IsRefusedByTheCheck()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await SeedAsync(db);

        // Act
        var act = async () => await db.ExecuteAsync(Insert(db.NewId(), "h-2", role: "owner"));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task DeleteHousehold_WithInvitations_CascadesThem()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await SeedAsync(db);
        await db.ExecuteAsync(Insert(db.NewId(), "h-3"));

        // Act
        await db.ExecuteAsync($"DELETE FROM households WHERE id = '{ServerDatabase.Household.Value}';");

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM invitations;")).Should().Be(0L);
    }

    internal static readonly Guid Owner = Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c1");

    internal static async Task SeedAsync(ServerDatabase db)
    {
        await db.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "EmailConfirmed", "DisplayName", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{Owner}', 'google:o', 'GOOGLE:O', 'o@example.com', true, 'Andrzej', false, false, false, 0);
            INSERT INTO households (id, next_seq, created_at, created_by) VALUES ('{ServerDatabase.Household.Value}', 1, now(), '{Owner}');
            INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{Owner}', 'owner', '2026-10-01 11:00:00+00');
            """);
    }

    private static string Insert(Guid id, string hash, string role = "parent") =>
        $"""
        INSERT INTO invitations (id, household_id, role, code_hash, created_by, created_at, expires_at)
        VALUES ('{id}', '{ServerDatabase.Household.Value}', '{role}', '{hash}', '{Owner}', now(), now() + interval '72 hours');
        """;
}
