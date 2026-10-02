using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// <c>device_sessions</c> (ADR 0003 §4). Every write is one statement: creating replaces the phone's row in place,
/// rotating is a compare-and-swap, revoking only stamps <c>revoked_at</c>. A row is never deleted here, so a phone that
/// signed out is still recognised as the same phone.
/// </summary>
internal sealed class DeviceSessionStore(NpgsqlDataSource dataSource) : IDeviceSessionStore
{
    private const string SelectColumns = "SELECT id, user_id, device_id, device_name, created_at, last_used_at, revoked_at";

    // Proposed tokens are client-chosen, so one row's previous hash can equal another row's current one: the current
    // match wins, or the wrong session would be taken for a reused token and revoked.
    private const string FindSql = SelectColumns + ", refresh_hash = @hash, refresh_hash FROM device_sessions "
        + "WHERE refresh_hash = @hash OR previous_refresh_hash = @hash ORDER BY (refresh_hash = @hash) DESC LIMIT 1;";

    private const string ListSql = SelectColumns + " FROM device_sessions WHERE user_id = @user ORDER BY created_at;";

    // The unique (user_id, device_id) index covers revoked rows too, so signing in again on a phone UPDATEs its row:
    // a new ID and hash, no previous hash, active again.
    private const string CreateSql =
        "INSERT INTO device_sessions (id, user_id, device_id, device_name, refresh_hash, created_at, last_used_at) "
        + "VALUES (@id, @user, @device, @name, @hash, @created_at, @last_used_at) "
        + "ON CONFLICT (user_id, device_id) DO UPDATE SET id = EXCLUDED.id, device_name = EXCLUDED.device_name, "
        + "refresh_hash = EXCLUDED.refresh_hash, previous_refresh_hash = NULL, created_at = EXCLUDED.created_at, "
        + "last_used_at = EXCLUDED.last_used_at, revoked_at = NULL;";

    public async Task CreateAsync(DeviceSession session, string refreshHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var command = dataSource.CreateCommand(CreateSql);
        command.Parameters.AddWithValue("id", session.Id.Value);
        command.Parameters.AddWithValue("user", session.UserId.Value);
        command.Parameters.AddWithValue("device", session.DeviceId.Value);
        command.Parameters.AddWithValue("name", session.DeviceName);
        command.Parameters.AddWithValue("hash", refreshHash);
        command.Parameters.AddWithValue("created_at", session.CreatedAt.UtcDateTime);
        command.Parameters.AddWithValue("last_used_at", session.LastUsedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(FindSql);
        command.Parameters.AddWithValue("hash", refreshHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new RefreshLookup(Read(reader), reader.GetBoolean(7) ? RefreshMatch.Current : RefreshMatch.Previous, reader.GetString(8));
    }

    public async Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE device_sessions SET previous_refresh_hash = refresh_hash, refresh_hash = @new, last_used_at = @now "
            + "WHERE id = @id AND refresh_hash = @expected AND revoked_at IS NULL;");
        command.Parameters.AddWithValue("new", newHash);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("expected", expectedHash);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The proposed next token's hash is another session's current one: the swap did not happen.
            return false;
        }
    }

    public async Task RevokeAsync(DeviceSessionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE device_sessions SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL;");
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("id", id.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsActiveAsync(DeviceSessionId id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM device_sessions WHERE id = @id AND revoked_at IS NULL);");
        command.Parameters.AddWithValue("id", id.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<IReadOnlyList<DeviceSession>> ListForUserAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ListSql);
        command.Parameters.AddWithValue("user", user.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<DeviceSession>();
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(Read(reader));
        return sessions;
    }

    public async Task<bool> ExistsForDeviceAsync(UserId user, DeviceId device, CancellationToken cancellationToken)
    {
        // Revoked rows count: a phone that signed out is still the same phone (Spec 010 rulings).
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM device_sessions WHERE user_id = @user AND device_id = @device);");
        command.Parameters.AddWithValue("user", user.Value);
        command.Parameters.AddWithValue("device", device.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static DeviceSession Read(NpgsqlDataReader reader) => new(
        new DeviceSessionId(reader.GetGuid(0)), new UserId(reader.GetGuid(1)), new DeviceId(reader.GetGuid(2)), reader.GetString(3),
        ServerColumns.Utc(reader.GetDateTime(4)), ServerColumns.Utc(reader.GetDateTime(5)),
        reader.IsDBNull(6) ? null : ServerColumns.Utc(reader.GetDateTime(6)));
}
