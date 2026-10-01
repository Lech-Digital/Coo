using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// A bulk rewrite of stored rows (ADR 0010 §8). It must be idempotent and work in batches: a crash resumes it.
/// It may change how data is stored, never what it means; each one ships with an equivalence test.
/// </summary>
internal interface IDataMigration
{
    /// <summary>Permanent, and ordered: for example <c>2027-03-01-diaper-v2</c>.</summary>
    string Id { get; }

    Task RunAsync(SqliteConnection connection, CancellationToken cancellationToken);
}

/// <summary>Runs every data migration not yet recorded, in ID order, and records each one when it completes.</summary>
internal sealed class DataMigrationRunner(IEnumerable<IDataMigration> migrations, TimeProvider time)
{
    public async Task RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var migration in migrations.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var done = await connection.ScalarAsync(
                "SELECT 1 FROM data_migrations WHERE id = $id;", cancellationToken, null, ("$id", migration.Id));
            if (done is not null)
                continue;

            await migration.RunAsync(connection, cancellationToken);
            await connection.ExecuteAsync(
                "INSERT INTO data_migrations (id, completed_utc_ms) VALUES ($id, $at);",
                cancellationToken, null, ("$id", migration.Id), ("$at", time.GetUtcNow().ToUnixTimeMilliseconds()));
        }
    }
}
