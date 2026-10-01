using Coo.Codecs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// Before the API takes a request (Spec 008 §4): apply the migrations, then register every (kind, version) the codecs
/// can read in <c>entry_kind_versions</c>. If either fails, start-up fails, and the container's log says why.
/// </summary>
internal sealed partial class DatabaseStartup(IDbContextFactory<CooDbContext> contexts, ILogger<DatabaseStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        var registered = await RegisterKindsAsync(context, cancellationToken);
        LogReady(logger, registered);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The idempotent insert of the codecs' catalogue (ADR 0010 G3). Returns how many pairs there are.</summary>
    internal static async Task<int> RegisterKindsAsync(CooDbContext context, CancellationToken cancellationToken)
    {
        var pairs = EntryDetailsCodecs.All
            .SelectMany(codec => Enumerable.Range(1, codec.CurrentVersion).Select(version => (Kind: codec.Kind.Value, Version: version)))
            .ToList();
        foreach (var (kind, version) in pairs)
        {
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO entry_kind_versions (kind, version) VALUES ({kind}, {version}) ON CONFLICT DO NOTHING;", cancellationToken);
        }

        return pairs.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database ready: migrations applied, {Count} kind versions registered")]
    private static partial void LogReady(ILogger logger, int count);
}
