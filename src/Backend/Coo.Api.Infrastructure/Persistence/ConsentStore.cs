using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary><c>consents</c> (Spec 011 §3). One statement; the primary key keeps the first consent to a wording.</summary>
internal sealed class ConsentStore(NpgsqlDataSource dataSource) : IConsentStore
{
    public async Task RecordAsync(ConsentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO consents (user_id, purpose, wording, given_at, app_version, language) VALUES (@u, @p, @w, @at, @v, @l) "
            + "ON CONFLICT (user_id, purpose, wording) DO NOTHING;");
        command.Parameters.AddWithValue("u", record.User.Value);
        command.Parameters.AddWithValue("p", record.Purpose);
        command.Parameters.AddWithValue("w", record.Wording);
        command.Parameters.AddWithValue("at", record.GivenAt.UtcDateTime);
        command.Parameters.AddWithValue("v", NpgsqlDbType.Varchar, (object?)record.AppVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("l", NpgsqlDbType.Varchar, (object?)record.Language ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ConsentRecord?> FindAsync(UserId user, string purpose, string wording, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT given_at, app_version, language FROM consents WHERE user_id = @u AND purpose = @p AND wording = @w;");
        command.Parameters.AddWithValue("u", user.Value);
        command.Parameters.AddWithValue("p", purpose);
        command.Parameters.AddWithValue("w", wording);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new ConsentRecord(user, purpose, wording, ServerColumns.Utc(reader.GetDateTime(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
