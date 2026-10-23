using System.Text.Json;
using Coo.Mobile.Application.AiLogging;
using Coo.Mobile.Infrastructure.AiLogging;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Pending notes in <c>app_state</c> (ADR 0005 §3): on this phone only, never synced; a note is a draft, not an entry.</summary>
internal sealed class PendingNoteStore(LocalDatabase database) : IPendingNoteStore, IDisposable
{
    private const string Key = "ai.pending_notes";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<PendingNote>> ListAsync(CancellationToken cancellationToken) => await ReadAsync(cancellationToken);

    public Task AddAsync(PendingNote note, CancellationToken cancellationToken) =>
        ChangeAsync(notes => notes.Add(note), cancellationToken);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeAsync(notes => notes.RemoveAll(n => n.Id == id), cancellationToken);

    private async Task ChangeAsync(Action<List<PendingNote>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var notes = await ReadAsync(cancellationToken);
            change(notes);
            await using var connection = await database.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                cancellationToken, null, ("$k", Key), ("$v", JsonSerializer.Serialize(notes, AiParsingJsonContext.Default.ListPendingNote)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PendingNote>> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var json = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", Key));
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize(json, AiParsingJsonContext.Default.ListPendingNote) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
