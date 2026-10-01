using Coo.Api.Application.Health;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>Answers <c>/health</c>: one <c>SELECT 1</c>, given three seconds.</summary>
internal sealed class DatabaseProbe(NpgsqlDataSource dataSource) : IDatabaseProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1;");
            return await command.ExecuteScalarAsync(timeout.Token) is 1;
        }
        catch (Exception e) when (e is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }
}
