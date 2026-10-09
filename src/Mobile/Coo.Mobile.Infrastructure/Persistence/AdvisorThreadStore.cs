using System.Text.Json;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Infrastructure.Advisor;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

internal sealed class AdvisorThreadStore(LocalDatabase database) : IAdvisorThreadStore
{
    private const string IndexKey = "advisor.threads";

    public async Task<IReadOnlyList<AdvisorThreadSummary>> ListThreadsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var json = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", IndexKey));
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var list = JsonSerializer.Deserialize(json, AdvisorJsonContext.Default.ListAdvisorThreadSummary);
            return list ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<AdvisorThread?> GetThreadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await database.OpenAsync(cancellationToken);
        var json = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", $"advisor.thread.{id}"));
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, AdvisorJsonContext.Default.AdvisorThread);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveThreadAsync(AdvisorThread thread, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentException.ThrowIfNullOrWhiteSpace(thread.Id);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // 1. Save thread content
        var threadJson = JsonSerializer.Serialize(thread, AdvisorJsonContext.Default.AdvisorThread);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$k", $"advisor.thread.{thread.Id}"), ("$v", threadJson));

        // 2. Update thread index
        var indexJson = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, transaction, ("$k", IndexKey));
        List<AdvisorThreadSummary> list;
        try
        {
            list = (!string.IsNullOrWhiteSpace(indexJson)
                ? JsonSerializer.Deserialize(indexJson, AdvisorJsonContext.Default.ListAdvisorThreadSummary)
                : null) ?? [];
        }
        catch
        {
            list = [];
        }

        var existingIdx = list.FindIndex(t => t.Id == thread.Id);
        var summary = new AdvisorThreadSummary
        {
            Id = thread.Id,
            Title = thread.Title,
            CreatedAt = thread.CreatedAt,
            LastUpdatedAt = thread.LastUpdatedAt,
            MessageCount = thread.Messages.Count,
        };

        if (existingIdx >= 0)
            list[existingIdx] = summary;
        else
            list.Insert(0, summary);

        list = list.OrderByDescending(t => t.LastUpdatedAt).ToList();
        var newIndexJson = JsonSerializer.Serialize(list, AdvisorJsonContext.Default.ListAdvisorThreadSummary);

        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$k", IndexKey), ("$v", newIndexJson));

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteThreadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync("DELETE FROM app_state WHERE key = $k;", cancellationToken, transaction, ("$k", $"advisor.thread.{id}"));

        var indexJson = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, transaction, ("$k", IndexKey));
        if (!string.IsNullOrWhiteSpace(indexJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize(indexJson, AdvisorJsonContext.Default.ListAdvisorThreadSummary);
                if (list != null)
                {
                    list.RemoveAll(t => t.Id == id);
                    var newIndexJson = JsonSerializer.Serialize(list, AdvisorJsonContext.Default.ListAdvisorThreadSummary);
                    await connection.ExecuteAsync(
                        "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                        cancellationToken, transaction, ("$k", IndexKey), ("$v", newIndexJson));
                }
            }
            catch
            {
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
