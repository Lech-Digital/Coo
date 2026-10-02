using Coo.Api.Application.Auth;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// <c>auth_nonces</c> (Spec 010 §4.2). Consuming is one conditional UPDATE, so a nonce is used at most once even when
/// two requests race.
/// </summary>
internal sealed class AuthNonceStore(NpgsqlDataSource dataSource) : IAuthNonceStore
{
    public async Task AddAsync(string nonceHash, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO auth_nonces (nonce_hash, issued_at) VALUES (@hash, @issued_at);");
        command.Parameters.AddWithValue("hash", nonceHash);
        command.Parameters.AddWithValue("issued_at", issuedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryConsumeAsync(string nonceHash, DateTimeOffset issuedNotBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE auth_nonces SET consumed_at = @now WHERE nonce_hash = @hash AND consumed_at IS NULL AND issued_at >= @not_before;");
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("hash", nonceHash);
        command.Parameters.AddWithValue("not_before", issuedNotBefore.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> DeleteIssuedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM auth_nonces WHERE issued_at < @cutoff;");
        command.Parameters.AddWithValue("cutoff", cutoff.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
