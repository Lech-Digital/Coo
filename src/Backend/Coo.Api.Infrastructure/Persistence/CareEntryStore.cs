using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Coo.Api.Application.CareLog;
using Coo.Codecs;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// The care log in PostgreSQL (Spec 008 §4). A write is one transaction: the previous details, the codec write gate,
/// the household's next <c>seq</c>, and the row. Reads go through the read guard; a row that does not decode is
/// logged with its ID and counted, never dropped.
/// </summary>
internal sealed partial class CareEntryStore(
    NpgsqlDataSource dataSource, IDbContextFactory<CooDbContext> contexts, ILogger<CareEntryStore> logger) : ICareEntryStore
{
    private static readonly Meter Meter = new("Coo.Api.Persistence");
    private static readonly Counter<long> Unreadable = Meter.CreateCounter<long>("coo.care_entries.unreadable");

    public async Task<long> SaveAsync(CareEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. The stored document, so the codec carries over properties this build does not know (Spec 003 §7).
        string? previous;
        await using (var select = new NpgsqlCommand("SELECT details::text FROM care_entries WHERE id = @id FOR UPDATE;", connection, transaction))
        {
            select.Parameters.AddWithValue("id", entry.Id.Value);
            previous = (string?)await select.ExecuteScalarAsync(cancellationToken);
        }

        // 2. The write gate. A DetailsEncodingException leaves the transaction uncommitted: nothing is written.
        var details = EntryDetailsCodecs.For(entry.Details).Encode(entry.Details, previous);

        // 3. The household's next seq. Updating its counter row serialises writes within one household (ADR 0002 §6).
        long seq;
        await using (var next = new NpgsqlCommand(
            "UPDATE households SET next_seq = next_seq + 1 WHERE id = @household RETURNING next_seq - 1;", connection, transaction))
        {
            next.Parameters.AddWithValue("household", entry.HouseholdId.Value);
            seq = await next.ExecuteScalarAsync(cancellationToken) is long value
                ? value
                : throw new HouseholdNotFoundException($"Household {entry.HouseholdId.Value} does not exist on the server.");
        }

        // 4. The row. An existing ID in another household is refused, never moved; a newer stored version is kept.
        await using (var upsert = new NpgsqlCommand(CareEntryWriter.UpsertSql, connection, transaction))
        {
            CareEntryWriter.AddParameters(upsert, entry, details, seq);
            if (await upsert.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"Entry {entry.Id.Value} belongs to another household, or a newer version is stored.");
        }

        await transaction.CommitAsync(cancellationToken);
        return seq;
    }

    public async Task<ServerStoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.CareEntries.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        return row is null ? null : Guard(row);
    }

    public async IAsyncEnumerable<ServerStoredEntry> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await foreach (var row in context.CareEntries.AsNoTracking().OrderBy(c => c.Id).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return Guard(row);
    }

    private ServerStoredEntry Guard(CareEntryRecord row)
    {
        var stored = ServerReadGuard.Classify(row);
        if (stored is UnreadableServerEntry unreadable)
        {
            Unreadable.Add(1);
            LogUnreadable(logger, unreadable.Id.Value, unreadable.Kind, unreadable.Reason);
        }

        return stored;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unreadable care entry {EntryId} (kind {Kind}): {Reason}")]
    private static partial void LogUnreadable(ILogger logger, Guid entryId, string kind, string reason);
}
