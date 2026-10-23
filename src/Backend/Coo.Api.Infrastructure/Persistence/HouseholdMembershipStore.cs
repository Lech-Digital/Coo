using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// Memberships and invitations (Spec 011b §10.3–§10.4). Every change runs in one transaction that first locks the
/// households' rows (<c>FOR UPDATE</c>, ascending ID order: PostgreSQL's <c>uuid</c> order is <see cref="GuidOrder"/>'s),
/// so two changes to one family serialise, and with a sync apply too (it locks the same counter row).
/// </summary>
internal sealed class HouseholdMembershipStore(NpgsqlDataSource dataSource) : IHouseholdMembershipStore
{
    public async Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(households);
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = new HashSet<HouseholdId>();
        if (households.Count > 0)
        {
            await using var command = new NpgsqlCommand("SELECT id FROM households WHERE id = ANY(@ids) ORDER BY id FOR UPDATE;", connection, transaction);
            command.Parameters.AddWithValue("ids", households.Select(h => h.Value).Distinct().ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                locked.Add(new HouseholdId(reader.GetGuid(0)));
        }

        var result = await work(new Transaction(connection, transaction, locked));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT m.user_id, u."DisplayName", coalesce(u."Email", ''), m.role, m.joined_at
            FROM memberships m JOIN "AspNetUsers" u ON u."Id" = m.user_id
            WHERE m.household_id = @h AND m.revoked_at IS NULL ORDER BY m.joined_at, m.user_id;
            """);
        command.Parameters.AddWithValue("h", household.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<MemberProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new MemberProfile(new UserId(reader.GetGuid(0)), reader.GetString(1), reader.GetString(2),
                RoleText.Parse(reader.GetString(3)), ServerColumns.Utc(reader.GetDateTime(4))));
        }

        return members;
    }

    public async Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, role, created_by, created_at, expires_at FROM invitations
            WHERE household_id = @h AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > @now ORDER BY created_at, id;
            """);
        command.Parameters.AddWithValue("h", household.Value);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var open = new List<OpenInvitation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            open.Add(new OpenInvitation(new InvitationId(reader.GetGuid(0)), RoleText.Parse(reader.GetString(1)), new UserId(reader.GetGuid(2)),
                ServerColumns.Utc(reader.GetDateTime(3)), ServerColumns.Utc(reader.GetDateTime(4))));
        }

        return open;
    }

    public async Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadMembershipsAsync(connection, null, "user_id = @u AND revoked_at IS NULL", ("u", user.Value), cancellationToken);
    }

    internal static async Task<IReadOnlyList<Membership>> ReadMembershipsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string where, (string Name, Guid Value) parameter, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT household_id, user_id, role, joined_at FROM memberships WHERE {where} ORDER BY joined_at, user_id;", connection, transaction);
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<Membership>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Membership.Create(new HouseholdId(reader.GetGuid(0)), new UserId(reader.GetGuid(1)), RoleText.Parse(reader.GetString(2)),
                ServerColumns.Utc(reader.GetDateTime(3))));
        }

        return list;
    }

    private static string RoleName(Role role) => role switch
    {
        Role.Owner => RoleText.Owner,
        Role.Parent => RoleText.Parent,
        Role.Caregiver => RoleText.Caregiver,
        Role.Viewer => RoleText.Viewer,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    private sealed class Transaction(NpgsqlConnection connection, NpgsqlTransaction transaction, HashSet<HouseholdId> locked) : IMembershipTransaction
    {
        public bool IsLocked(HouseholdId household) => locked.Contains(household);

        public async Task<MembershipLookup> FindMembershipAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            await using var command = Command("SELECT role, joined_at, revoked_at FROM memberships WHERE household_id = @h AND user_id = @u FOR UPDATE;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return MembershipLookup.None;
            var membership = Membership.Create(household, user, RoleText.Parse(reader.GetString(0)), ServerColumns.Utc(reader.GetDateTime(1)));
            return new MembershipLookup(membership, IsRevoked: !reader.IsDBNull(2), RevokedAt: reader.IsDBNull(2) ? null : ServerColumns.Utc(reader.GetDateTime(2)));
        }

        public Task<IReadOnlyList<Membership>> ActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
            ReadMembershipsAsync(connection, transaction, "household_id = @h AND revoked_at IS NULL", ("h", household.Value), cancellationToken);

        public Task<IReadOnlyList<Membership>> ActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
            ReadMembershipsAsync(connection, transaction, "user_id = @u AND revoked_at IS NULL", ("u", user.Value), cancellationToken);

        public async Task<Invitation?> FindInvitationForUpdateAsync(string codeHash, CancellationToken cancellationToken)
        {
            await using var command = Command($"SELECT {InvitationStore.Columns} FROM invitations i WHERE i.code_hash = @hash FOR UPDATE;");
            command.Parameters.AddWithValue("hash", codeHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? InvitationStore.Read(reader) : null;
        }

        public async Task<Invitation?> FindInvitationForUpdateAsync(HouseholdId household, InvitationId id, CancellationToken cancellationToken)
        {
            await using var command = Command($"SELECT {InvitationStore.Columns} FROM invitations i WHERE i.id = @id AND i.household_id = @h FOR UPDATE;");
            command.Parameters.AddWithValue("id", id.Value);
            command.Parameters.AddWithValue("h", household.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? InvitationStore.Read(reader) : null;
        }

        public async Task<int> CountOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "SELECT count(*) FROM invitations WHERE household_id = @h AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > @now;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("now", now.UtcDateTime);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task AddInvitationAsync(Invitation invitation, string codeHash, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invitation);
            await using var command = Command(
                """
                INSERT INTO invitations (id, household_id, role, code_hash, created_by, created_at, expires_at)
                VALUES (@id, @h, @role, @hash, @by, @at, @expires);
                """);
            command.Parameters.AddWithValue("id", invitation.Id.Value);
            command.Parameters.AddWithValue("h", invitation.HouseholdId.Value);
            command.Parameters.AddWithValue("role", RoleName(invitation.Role));
            command.Parameters.AddWithValue("hash", codeHash);
            command.Parameters.AddWithValue("by", invitation.CreatedBy.Value);
            command.Parameters.AddWithValue("at", invitation.CreatedAt.UtcDateTime);
            command.Parameters.AddWithValue("expires", invitation.ExpiresAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invitation);
            await using var command = Command("UPDATE invitations SET accepted_by = @by, accepted_at = @at, revoked_at = @revoked WHERE id = @id;");
            command.Parameters.AddWithValue("id", invitation.Id.Value);
            command.Parameters.AddWithValue("by", NpgsqlDbType.Uuid, (object?)invitation.AcceptedBy?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, (object?)invitation.AcceptedAt?.UtcDateTime ?? DBNull.Value);
            command.Parameters.AddWithValue("revoked", NpgsqlDbType.TimestampTz, (object?)invitation.RevokedAt?.UtcDateTime ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(membership);
            await using var command = Command("INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES (@h, @u, @role, @at);");
            command.Parameters.AddWithValue("h", membership.HouseholdId.Value);
            command.Parameters.AddWithValue("u", membership.UserId.Value);
            command.Parameters.AddWithValue("role", RoleName(membership.Role));
            command.Parameters.AddWithValue("at", membership.JoinedAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task ReactivateMembershipAsync(HouseholdId household, UserId user, Role role, DateTimeOffset joinedAt, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE memberships SET revoked_at = NULL, role = @role, joined_at = @at WHERE household_id = @h AND user_id = @u;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            command.Parameters.AddWithValue("role", RoleName(role));
            command.Parameters.AddWithValue("at", joinedAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task RevokeMembershipAsync(HouseholdId household, UserId user, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE memberships SET revoked_at = @now WHERE household_id = @h AND user_id = @u AND revoked_at IS NULL;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            command.Parameters.AddWithValue("now", now.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> WithdrawOpenInvitationsByAsync(HouseholdId household, UserId createdBy, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE invitations SET revoked_at = @now WHERE household_id = @h AND created_by = @u AND accepted_at IS NULL AND revoked_at IS NULL;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", createdBy.Value);
            command.Parameters.AddWithValue("now", now.UtcDateTime);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> PromoteToOwnerAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE memberships SET role = 'owner' WHERE household_id = @h AND user_id = @u AND revoked_at IS NULL;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<bool> HoldsAnyRecordAsync(HouseholdId household, CancellationToken cancellationToken)
        {
            await using var command = Command(
                """
                SELECT EXISTS (SELECT 1 FROM care_entries WHERE household_id = @h) OR EXISTS (SELECT 1 FROM baby_profiles WHERE household_id = @h)
                    OR EXISTS (SELECT 1 FROM household_settings WHERE household_id = @h) OR EXISTS (SELECT 1 FROM duplicate_dismissals WHERE household_id = @h);
                """);
            command.Parameters.AddWithValue("h", household.Value);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async Task<bool> TryCreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var insert = Command(
                "INSERT INTO households (id, next_seq, created_at, created_by) VALUES (@h, 1, @now, @u) ON CONFLICT (id) DO NOTHING;");
            insert.Parameters.AddWithValue("h", household.Value);
            insert.Parameters.AddWithValue("now", now.UtcDateTime);
            insert.Parameters.AddWithValue("u", owner.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                return false;
            await AddMembershipAsync(Membership.Create(household, owner, Role.Owner, now), cancellationToken);
            return true;
        }

        public async Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
        {
            // care_entries restricts the household's deletion (Spec 008 §4); everything else cascades.
            await using var entries = Command("DELETE FROM care_entries WHERE household_id = @h;");
            entries.Parameters.AddWithValue("h", household.Value);
            await entries.ExecuteNonQueryAsync(cancellationToken);
            await using var row = Command("DELETE FROM households WHERE id = @h;");
            row.Parameters.AddWithValue("h", household.Value);
            await row.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken)
        {
            await using var command = Command("DELETE FROM memberships WHERE user_id = @u;");
            command.Parameters.AddWithValue("u", user.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteUserAsync(UserId user, CancellationToken cancellationToken)
        {
            // The Advisor's daily question counts have no foreign key: they go explicitly, as the privacy policy says.
            await using var usage = Command("DELETE FROM advisor_usages WHERE user_id = @u;");
            usage.Parameters.AddWithValue("u", user.Value);
            await usage.ExecuteNonQueryAsync(cancellationToken);

            // Logins, claims, tokens, device sessions, consents and invitations sent go with the user (ON DELETE CASCADE);
            // households.created_by and invitations.accepted_by become NULL.
            await using var command = Command("""DELETE FROM "AspNetUsers" WHERE "Id" = @u;""");
            command.Parameters.AddWithValue("u", user.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private NpgsqlCommand Command(string sql) => new(sql, connection, transaction);
    }
}
