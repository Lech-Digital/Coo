using System.Globalization;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary><c>family_memberships</c> and the join's facts in <c>app_state</c> (Spec 011b §4.1, §5.3, §6.2, §7.1).</summary>
internal sealed class FamilyStore(LocalDatabase database) : IFamilyStore
{
    internal const string FamiliesUserKey = "families_user";
    internal const string PendingKey = "pending_invitation";
    internal const string JoiningHouseholdKey = "joining_household";
    internal const string JoiningSourceKey = "joining_source";
    internal const string JoiningInviterKey = "joining_inviter";

    internal static string CopiedKey(HouseholdId source, HouseholdId target) => $"copied_from:{Columns.Id(source.Value)}:{Columns.Id(target.Value)}";

    public async Task<IReadOnlyList<FamilyMembership>?> ListAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        if ((string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", FamiliesUserKey))
            != Columns.Id(user.Value))
            return null;
        await using var command = connection.Command(
            "SELECT household_id, role, joined_utc_ms, owner_name, member_count FROM family_memberships ORDER BY joined_utc_ms, household_id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<FamilyMembership>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(0)) is not { } id || RoleNames.Parse(reader.GetString(1)) is not { } role)
                continue;
            list.Add(new FamilyMembership(new HouseholdId(id), role, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetString(3), (int)reader.GetInt64(4)));
        }

        return list;
    }

    public async Task ReplaceAsync(UserId user, IReadOnlyList<FamilyMembership> memberships, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM family_memberships;", cancellationToken, transaction);
        foreach (var m in memberships)
        {
            await connection.ExecuteAsync(
                "INSERT INTO family_memberships (household_id, role, joined_utc_ms, owner_name, member_count) VALUES ($h, $role, $at, $owner, $count);",
                cancellationToken, transaction,
                ("$h", Columns.Id(m.Household.Value)), ("$role", RoleNames.Text(m.Role)), ("$at", m.JoinedAt.ToUnixTimeMilliseconds()),
                ("$owner", m.OwnerName), ("$count", Math.Max(1, m.MemberCount)));
        }

        await SetAsync(connection, transaction, FamiliesUserKey, Columns.Id(user.Value), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM family_memberships;", cancellationToken, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM app_state WHERE key IN ($u, $h, $s, $i);", cancellationToken, transaction,
            ("$u", FamiliesUserKey), ("$h", JoiningHouseholdKey), ("$s", JoiningSourceKey), ("$i", JoiningInviterKey));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PendingInvitation?> GetPendingInvitationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var value = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", PendingKey));
        var parts = value?.Split('|');
        if (parts is not [var code, var at] || InvitationCode.Parse(code) is not { IsSuccess: true } parsed
            || !long.TryParse(at, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            return null;
        return new PendingInvitation(parsed.Value, DateTimeOffset.FromUnixTimeMilliseconds(ms));
    }

    public async Task SetPendingInvitationAsync(PendingInvitation? invitation, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        if (invitation is null)
            await connection.ExecuteAsync("DELETE FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", PendingKey));
        else
            await SetAsync(connection, null, PendingKey,
                invitation.Code.Value + "|" + invitation.ArrivedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task<JoiningMarker?> GetJoiningAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var joined = Columns.ParseGuid((string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", JoiningHouseholdKey)));
        var source = Columns.ParseGuid((string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", JoiningSourceKey)));
        var inviter = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", JoiningInviterKey));
        return joined is { } j && source is { } s ? new JoiningMarker(new HouseholdId(j), new HouseholdId(s), inviter ?? "") : null;
    }

    public async Task SetJoiningAsync(JoiningMarker? marker, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (marker is null)
        {
            await connection.ExecuteAsync(
                "DELETE FROM app_state WHERE key IN ($h, $s, $i);", cancellationToken, transaction,
                ("$h", JoiningHouseholdKey), ("$s", JoiningSourceKey), ("$i", JoiningInviterKey));
        }
        else
        {
            await SetAsync(connection, transaction, JoiningHouseholdKey, Columns.Id(marker.Joined.Value), cancellationToken);
            await SetAsync(connection, transaction, JoiningSourceKey, Columns.Id(marker.Source.Value), cancellationToken);
            await SetAsync(connection, transaction, JoiningInviterKey, marker.InviterName, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> WasCopiedAsync(HouseholdId source, HouseholdId target, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await connection.ScalarAsync("SELECT 1 FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", CopiedKey(source, target))) is not null;
    }

    private static async Task SetAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, string value, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$k", key), ("$v", value));
}
