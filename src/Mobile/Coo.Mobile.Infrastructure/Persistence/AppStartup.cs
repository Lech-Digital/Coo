using Coo.Mobile.Application.CareLog;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// Everything that runs once before the first page (Spec 004 §7): schema migrations, data migrations, the
/// integrity scan once per app version, and the device identity.
/// </summary>
internal sealed class AppStartup(LocalDatabase database, DataMigrationRunner dataMigrations, DeviceState device, string appVersion) : IAppStartup
{
    private const int ScanBatchSize = 500;

    public async Task<StartupResult> RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        try
        {
            await SchemaMigrator.MigrateAsync(connection, cancellationToken);
        }
        catch (DatabaseFromNewerAppException)
        {
            return StartupResult.DatabaseFromNewerApp;
        }

        await dataMigrations.RunAsync(connection, cancellationToken);
        await IntegrityScanAsync(connection, appVersion, cancellationToken);
        await device.EnsureCreatedAsync(cancellationToken);
        return StartupResult.Ready;
    }

    /// <summary>
    /// The integrity scan (G5): once per app version, re-classify every row, in both directions. A row this
    /// version can now read becomes ok again.
    /// </summary>
    internal static async Task<bool> IntegrityScanAsync(SqliteConnection connection, string appVersion, CancellationToken cancellationToken)
    {
        var scanned = (string?)await connection.ScalarAsync(
            "SELECT value FROM app_state WHERE key = 'last_integrity_scan_version';", cancellationToken);
        if (scanned == appVersion)
            return false;

        var afterId = string.Empty;
        while (true)
        {
            var rows = await CareLogStore.ReadRowsAsync(
                connection,
                $"SELECT {CareEntryRow.SelectColumns} FROM care_entries WHERE id > $after ORDER BY id LIMIT {ScanBatchSize};",
                cancellationToken,
                ("$after", afterId));
            if (rows.Count == 0)
                break;

            await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (var row in rows)
                {
                    var classified = CareEntryClassifier.Classify(row);
                    if (classified.Integrity != row.Integrity)
                    {
                        await connection.ExecuteAsync(
                            "UPDATE care_entries SET integrity = $integrity, integrity_reason = $reason WHERE id = $id;",
                            cancellationToken, transaction,
                            ("$integrity", classified.Integrity), ("$reason", classified.Reason), ("$id", row.Id));
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            }

            afterId = rows[^1].Id;
        }

        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ('last_integrity_scan_version', $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$v", appVersion));
        return true;
    }
}
