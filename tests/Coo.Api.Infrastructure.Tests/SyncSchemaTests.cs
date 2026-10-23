using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011 §7.2: the new tables, their guards, and an upgrade of a database from Increment 10.</summary>
[Collection(PostgresTests.Name)]
public sealed class SyncSchemaTests(PostgresServer server)
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheSyncColumns()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var columns = (string)(await db.ScalarAsync(
            """
            SELECT string_agg(table_name || '.' || column_name, ',')
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (table_name IN ('baby_profiles', 'household_settings', 'duplicate_dismissals', 'applied_changes', 'sync_conflicts', 'consents')
                   OR (table_name = 'memberships' AND column_name = 'revoked_at'));
            """))!;

        // Assert
        columns.Split(',').Should().BeEquivalentTo(
            "baby_profiles.id", "baby_profiles.household_id", "baby_profiles.name", "baby_profiles.nicknames", "baby_profiles.birth_date",
            "baby_profiles.due_date", "baby_profiles.version_wall_ms", "baby_profiles.version_counter", "baby_profiles.version_device",
            "baby_profiles.is_deleted", "baby_profiles.seq",
            "household_settings.household_id", "household_settings.volume_unit", "household_settings.time_zone_id",
            "household_settings.hidden_kinds", "household_settings.version_wall_ms", "household_settings.version_counter",
            "household_settings.version_device", "household_settings.is_deleted", "household_settings.seq",
            "duplicate_dismissals.id", "duplicate_dismissals.household_id", "duplicate_dismissals.entry_a", "duplicate_dismissals.entry_b",
            "duplicate_dismissals.version_wall_ms", "duplicate_dismissals.version_counter", "duplicate_dismissals.version_device",
            "duplicate_dismissals.is_deleted", "duplicate_dismissals.seq",
            "applied_changes.change_id", "applied_changes.result", "applied_changes.reason", "applied_changes.applied_at",
            "sync_conflicts.id", "sync_conflicts.household_id", "sync_conflicts.change_id", "sync_conflicts.entity_type",
            "sync_conflicts.entity_id", "sync_conflicts.winner_wall_ms", "sync_conflicts.winner_counter", "sync_conflicts.winner_device",
            "sync_conflicts.loser_wall_ms", "sync_conflicts.loser_counter", "sync_conflicts.loser_device", "sync_conflicts.recorded_at",
            "consents.user_id", "consents.purpose", "consents.wording", "consents.given_at", "consents.app_version", "consents.language",
            "memberships.revoked_at");
    }

    [Fact]
    public async Task Migrate_FromSignInAndSessionsWithData_KeepsEveryRow()
    {
        // Arrange: production as Increment 10 left it, with a care entry.
        await using var db = await ServerDatabase.CreateAsync(server, migrate: false);
        await db.MigrateToAsync("20261002143234_SignInAndSessions");
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        await db.ExecuteAsync("INSERT INTO entry_kind_versions (kind, version) VALUES ('diaper', 1) ON CONFLICT DO NOTHING;");
        await db.ExecuteAsync($"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_at, start_offset_min, logged_by, source,
                details, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{db.NewId()}', '{ServerDatabase.Household.Value}', 'diaper', '{ServerDatabase.Baby.Value}', false, now(), 120,
                'user:7a1c0000-0000-7000-8000-0000000000c1', 'manual', '{"{"}"v":1,"contents":"wet"{"}"}', 1, 0, '{ServerDatabase.Device.Value}', false, 1);
            """);

        // Act
        await db.MigrateAsync();

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(1L);
    }

    [Theory]
    [InlineData("INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq) VALUES ('{H}', 'litres', 'Europe/Amsterdam', '{}', 1, 0, '{D}', false, 1);")]
    [InlineData("INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted, seq) VALUES ('{N}', '{H}', '{D}', '{D}', 1, 0, '{D}', false, 1);")]
    [InlineData("INSERT INTO applied_changes (change_id, result, reason, applied_at) VALUES ('{N}', 'maybe', NULL, now());")]
    public async Task RawInsert_AgainstAGuard_IsRefusedByTheDatabase(string sql)
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var statement = sql.Replace("{H}", ServerDatabase.Household.Value.ToString(), StringComparison.Ordinal)
            .Replace("{D}", ServerDatabase.Device.Value.ToString(), StringComparison.Ordinal)
            .Replace("{N}", db.NewId().ToString(), StringComparison.Ordinal);

        // Act
        var act = async () => await db.ExecuteAsync(statement);

        // Assert
        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task RawInsert_SecondRowWithTheSameSeqAcrossOneTable_IsRefused()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        string Insert() => $"""
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{db.NewId()}', '{ServerDatabase.Household.Value}', 'Lena', '{"{}"}', '2026-08-20', 1, 0, '{ServerDatabase.Device.Value}', false, 7);
            """;
        await db.ExecuteAsync(Insert());

        // Act
        var act = async () => await db.ExecuteAsync(Insert());

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task DeleteHousehold_WithSyncedRows_CascadesToTheNewTables()
    {
        // Arrange: account deletion removes a household; its profiles, settings, dismissals and conflicts go with it.
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        await db.ExecuteAsync($"""
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{ServerDatabase.Household.Value}', 'ml', 'Europe/Amsterdam', '{"{}"}', 1, 0, '{ServerDatabase.Device.Value}', false, 1);
            INSERT INTO sync_conflicts (household_id, change_id, entity_type, entity_id, winner_wall_ms, winner_counter, winner_device,
                loser_wall_ms, loser_counter, loser_device, recorded_at)
            VALUES ('{ServerDatabase.Household.Value}', '{db.NewId()}', 'CareEntry', '{db.NewId()}', 2, 0, '{ServerDatabase.Device.Value}',
                1, 0, '{ServerDatabase.Device.Value}', now());
            """);

        // Act
        await db.ExecuteAsync($"DELETE FROM households WHERE id = '{ServerDatabase.Household.Value}';");

        // Assert
        (await db.ScalarAsync("SELECT (SELECT count(*) FROM household_settings) + (SELECT count(*) FROM sync_conflicts);")).Should().Be(0L);
    }

    [Fact]
    public async Task DeleteUser_WithAConsent_CascadesToConsents()
    {
        // Arrange: account deletion removes the user; the consent record goes with it.
        await using var db = await ServerDatabase.CreateAsync(server);
        var user = db.NewId();
        await db.ExecuteAsync($"""
            INSERT INTO "AspNetUsers" ("Id", "DisplayName", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{user}', 'A', true, false, false, false, 0);
            INSERT INTO consents (user_id, purpose, wording, given_at, app_version, language) VALUES ('{user}', 'care-data', 'care-data-v1', now(), '0.11.0', 'en');
            """);

        // Act
        await db.ExecuteAsync($"""DELETE FROM "AspNetUsers" WHERE "Id" = '{user}';""");

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM consents;")).Should().Be(0L);
    }

    [Fact]
    public async Task Upsert_VersionGuardedOnConflict_KeepsTheNewerStampOnly()
    {
        // Arrange: the shape Task 5's stores use (PF1): ON CONFLICT on the key, guarded by the stored version.
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var id = db.NewId();
        string Upsert(string name, long wall, long seq) => $"""
            INSERT INTO baby_profiles AS t (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{id}', '{ServerDatabase.Household.Value}', '{name}', '{"{}"}', '2026-08-20', {wall}, 0, '{ServerDatabase.Device.Value}', false, {seq})
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, version_wall_ms = excluded.version_wall_ms,
                version_counter = excluded.version_counter, version_device = excluded.version_device, seq = excluded.seq
            WHERE (t.version_wall_ms, t.version_counter, t.version_device)
                < (excluded.version_wall_ms, excluded.version_counter, excluded.version_device);
            """;
        await db.ExecuteAsync(Upsert("Lena", 2, 1));

        // Act
        var older = await db.ExecuteAsync(Upsert("Old", 1, 2));
        var newer = await db.ExecuteAsync(Upsert("New", 3, 3));

        // Assert
        older.Should().Be(0);
        newer.Should().Be(1);
        (await db.ScalarAsync($"SELECT name FROM baby_profiles WHERE id = '{id}';")).Should().Be("New");
    }
}
