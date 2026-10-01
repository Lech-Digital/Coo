using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// Applies the numbered SQL scripts in <c>Persistence/Migrations</c> (Spec 004 §7.1). Each script runs in its own
/// transaction together with <c>PRAGMA user_version</c>, so the schema is always at a whole version.
/// </summary>
internal static partial class SchemaMigrator
{
    public static IReadOnlyList<MigrationScript> Scripts { get; } = LoadScripts();

    public static int LatestVersion => Scripts[^1].Number;

    public static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var current = await GetUserVersionAsync(connection, cancellationToken);
        if (current > LatestVersion)
            throw new DatabaseFromNewerAppException(current, LatestVersion);

        foreach (var script in Scripts.Where(s => s.Number > current))
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(script.Sql, cancellationToken, transaction);
            await connection.ExecuteAsync(
                string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {script.Number};"), cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        Convert.ToInt32(await connection.ScalarAsync("PRAGMA user_version;", cancellationToken), CultureInfo.InvariantCulture);

    private static List<MigrationScript> LoadScripts()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Match: ScriptName().Match(name)))
            .Where(r => r.Match.Success)
            .Select(r => new MigrationScript(
                int.Parse(r.Match.Groups["number"].Value, CultureInfo.InvariantCulture),
                r.Match.Groups["file"].Value,
                Read(assembly, r.Name)))
            .OrderBy(s => s.Number)
            .ToList();

        if (scripts.Count == 0 || scripts.Select((s, i) => s.Number == i + 1).Any(ok => !ok))
            throw new InvalidOperationException("Migration scripts must be numbered 1, 2, 3… with no gaps.");

        return scripts;
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"Migrations\.(?<file>(?<number>\d{4})-[a-z0-9-]+\.sql)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptName();
}

internal sealed record MigrationScript(int Number, string FileName, string Sql);

/// <summary>
/// The database was written by a newer build (a downgrade, or a restored newer backup). It is not opened for
/// writing: the app asks for an update and touches nothing (Spec 004 §7.1).
/// </summary>
internal sealed class DatabaseFromNewerAppException(int databaseVersion, int appVersion)
    : InvalidOperationException($"The database is at schema version {databaseVersion}; this build knows up to {appVersion}.")
{
    public int DatabaseVersion { get; } = databaseVersion;

    public int AppVersion { get; } = appVersion;
}
