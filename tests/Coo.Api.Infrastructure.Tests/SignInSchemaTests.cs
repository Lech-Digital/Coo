using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 010 §4.2: the new tables and columns, and an upgrade of a database from before this increment.</summary>
[Collection(PostgresTests.Name)]
public sealed class SignInSchemaTests(PostgresServer server)
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheSignInColumns()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var columns = await db.ScalarAsync(
            """
            SELECT string_agg(table_name || '.' || column_name, ',')
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (table_name IN ('auth_nonces', 'device_sessions', 'memberships')
                   OR (table_name = 'households' AND column_name = 'created_by'));
            """);

        // Assert
        ((string)columns!).Split(',').Should().BeEquivalentTo(
            "auth_nonces.nonce_hash", "auth_nonces.issued_at", "auth_nonces.consumed_at",
            "device_sessions.id", "device_sessions.user_id", "device_sessions.device_id", "device_sessions.device_name",
            "device_sessions.refresh_hash", "device_sessions.previous_refresh_hash", "device_sessions.created_at",
            "device_sessions.last_used_at", "device_sessions.revoked_at",
            "memberships.household_id", "memberships.user_id", "memberships.role", "memberships.joined_at",
            "households.created_by");
    }

    [Fact]
    public async Task Migrate_FromTheInitialSchemaWithAHousehold_KeepsItWithoutACreator()
    {
        // Arrange: production before Increment 10 may hold households already.
        await using var db = await ServerDatabase.CreateAsync(server, migrate: false);
        await db.MigrateToAsync("20261001131623_InitialSchema");
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);

        // Act
        await db.MigrateAsync();

        // Assert
        (await db.ScalarAsync($"SELECT created_by IS NULL FROM households WHERE id = '{ServerDatabase.Household.Value}';"))
            .Should().Be(true);
    }

    [Fact]
    public async Task Insert_SecondOwnerInOneHousehold_IsRefusedByTheDatabase()
    {
        // Arrange: exactly one Owner per household (ADR 0003 §6), whatever code tries.
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var first = db.NewId();
        var second = db.NewId();
        await db.ExecuteAsync($"""
            {InsertUsers(first, second)}
            INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{first}', 'owner', now());
            """);

        // Act
        Func<Task> act = () => db.ExecuteAsync(
            $"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{second}', 'owner', now());");

        // Assert
        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Insert_SameGoogleLoginForASecondUser_IsRefusedByTheDatabase()
    {
        // Arrange: two concurrent first sign-ins with one Google account must not make two users.
        await using var db = await ServerDatabase.CreateAsync(server);
        var first = db.NewId();
        var second = db.NewId();
        await db.ExecuteAsync($"""
            {InsertUsers(first, second)}
            INSERT INTO "AspNetUserLogins" ("LoginProvider", "ProviderKey", "UserId") VALUES ('Google', 'sub-1', '{first}');
            """);

        // Act
        Func<Task> act = () => db.ExecuteAsync(
            $"""INSERT INTO "AspNetUserLogins" ("LoginProvider", "ProviderKey", "UserId") VALUES ('Google', 'sub-1', '{second}');""");

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Insert_SecondSessionForARevokedUserAndDevice_IsRefusedByTheDatabase()
    {
        // Arrange: a revoked session keeps its row, so the phone's next sign-in finds and replaces it.
        await using var db = await ServerDatabase.CreateAsync(server);
        var user = db.NewId();
        await db.ExecuteAsync($"""
            {InsertUsers(user)}
            INSERT INTO device_sessions (id, user_id, device_id, device_name, refresh_hash, created_at, last_used_at, revoked_at)
            VALUES ('{db.NewId()}', '{user}', '{ServerDatabase.Device.Value}', 'Pixel', 'hash-1', now(), now(), now());
            """);

        // Act
        Func<Task> act = () => db.ExecuteAsync($"""
            INSERT INTO device_sessions (id, user_id, device_id, device_name, refresh_hash, created_at, last_used_at)
            VALUES ('{db.NewId()}', '{user}', '{ServerDatabase.Device.Value}', 'Pixel', 'hash-2', now(), now());
            """);

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Update_ConsumeANonceTwice_SucceedsOnlyOnce()
    {
        // Arrange: a nonce is consumed by one conditional UPDATE (Spec 010 §4.2).
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.ExecuteAsync("INSERT INTO auth_nonces (nonce_hash, issued_at) VALUES ('nonce-1', now());");
        const string consume =
            "UPDATE auth_nonces SET consumed_at = now() WHERE nonce_hash = 'nonce-1' AND consumed_at IS NULL AND issued_at > now() - interval '5 minutes';";

        // Act
        var first = await db.ExecuteAsync(consume);
        var second = await db.ExecuteAsync(consume);

        // Assert
        first.Should().Be(1);
        second.Should().Be(0);
    }

    private static string InsertUsers(params Guid[] ids) =>
        """INSERT INTO "AspNetUsers" ("Id", "DisplayName", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount") VALUES """
        + string.Join(", ", ids.Select(id => $"('{id}', 'A', true, false, false, false, 0)"))
        + ";";
}
