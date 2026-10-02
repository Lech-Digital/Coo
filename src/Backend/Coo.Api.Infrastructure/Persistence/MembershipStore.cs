using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>The per-request membership check (Spec 011 §7.1): one indexed lookup by the primary key.</summary>
internal sealed class MembershipStore(NpgsqlDataSource dataSource) : IMembershipStore
{
    public async Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT role, joined_at, revoked_at FROM memberships WHERE household_id = @h AND user_id = @u;");
        command.Parameters.AddWithValue("h", household.Value);
        command.Parameters.AddWithValue("u", user.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return MembershipLookup.None;
        var membership = Membership.Create(household, user, RoleText.Parse(reader.GetString(0)), ServerColumns.Utc(reader.GetDateTime(1)));
        return new MembershipLookup(membership, IsRevoked: !reader.IsDBNull(2));
    }
}
