using Coo.Api.Application.CareLog;
using Coo.Domain.Common;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>Creates a household's row and its <c>seq</c> counter (ADR 0002 §6). Idempotent.</summary>
internal sealed class HouseholdRegistry(NpgsqlDataSource dataSource, TimeProvider time) : IHouseholdRegistry
{
    public async Task EnsureAsync(HouseholdId id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO households (id, next_seq, created_at) VALUES (@id, 1, @created_at) ON CONFLICT (id) DO NOTHING;");
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("created_at", time.GetUtcNow().UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
