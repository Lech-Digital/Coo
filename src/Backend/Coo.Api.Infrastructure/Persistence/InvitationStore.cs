using Coo.Api.Application.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary><c>invitations</c> outside a transaction (Spec 011b §10.3): the preview's reads and the daily purge.</summary>
internal sealed class InvitationStore(NpgsqlDataSource dataSource) : IInvitationStore
{
    internal const string Columns = "i.id, i.household_id, i.role, i.created_by, i.created_at, i.expires_at, i.accepted_by, i.accepted_at, i.revoked_at";

    public async Task<FoundInvitation?> FindByHashAsync(string codeHash, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""SELECT {Columns}, u."DisplayName" FROM invitations i JOIN "AspNetUsers" u ON u."Id" = i.created_by WHERE i.code_hash = @hash;""");
        command.Parameters.AddWithValue("hash", codeHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new FoundInvitation(Read(reader), reader.GetString(9)) : null;
    }

    public async Task<IReadOnlyList<string>> ListBabyNamesAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT name FROM baby_profiles WHERE household_id = @h AND NOT is_deleted ORDER BY coalesce(birth_date, due_date), name;");
        command.Parameters.AddWithValue("h", household.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM invitations WHERE expires_at < @now;");
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The first nine columns of <see cref="Columns"/>, in order.</summary>
    internal static Invitation Read(NpgsqlDataReader reader) => Invitation.Rehydrate(
        new InvitationId(reader.GetGuid(0)), new HouseholdId(reader.GetGuid(1)), RoleText.Parse(reader.GetString(2)), new UserId(reader.GetGuid(3)),
        ServerColumns.Utc(reader.GetDateTime(4)), ServerColumns.Utc(reader.GetDateTime(5)),
        reader.IsDBNull(6) ? null : new UserId(reader.GetGuid(6)),
        reader.IsDBNull(7) ? null : ServerColumns.Utc(reader.GetDateTime(7)),
        reader.IsDBNull(8) ? null : ServerColumns.Utc(reader.GetDateTime(8)));
}
