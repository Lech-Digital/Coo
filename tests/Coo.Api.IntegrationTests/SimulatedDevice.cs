using System.Net.Http.Json;
using System.Text.Json;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.IntegrationTests;

/// <summary>
/// A phone reduced to the protocol (ADR 0002): diapers with a note, an outbox, push, pull. Its wall clock is moved by
/// the test, so "later" is exact. It shares no code with the app: it checks the server against the ADR, not against the
/// phone's own implementation.
/// </summary>
internal sealed class SimulatedDevice(HttpClient client, string accessToken, Guid household, Guid device, int number, long startWallMs)
{
    private readonly HybridLogicalClock _clock = new(new DeviceId(device));
    private readonly List<(Guid ChangeId, Guid EntityId)> _outbox = [];
    private long _wall = startWallMs;
    private long _cursor;
    private long? _head;
    private int _changes;

    public sealed record Diaper(Guid Id, HlcStamp Version, bool IsDeleted, string? Note);

    public Dictionary<Guid, Diaper> Records { get; } = [];

    public int Pending => _outbox.Count;

    public void Wait(TimeSpan time) => _wall += (long)time.TotalMilliseconds;

    public void Log(Guid id, string note) => Write(new Diaper(id, Tick(), false, note));

    public void Edit(Guid id, string note) => Write(Records[id] with { Version = Tick(), Note = note });

    public void Delete(Guid id) => Write(Records[id] with { Version = Tick(), IsDeleted = true });

    /// <summary>The live entries, by ID, with their notes: what the timeline shows.</summary>
    public IReadOnlyDictionary<Guid, string?> Timeline() =>
        Records.Values.Where(r => !r.IsDeleted).ToDictionary(r => r.Id, r => r.Note);

    public async Task PushAsync()
    {
        // One change per record: its current state under its newest change ID (Spec 011 §6.1).
        var batch = _outbox.GroupBy(o => o.EntityId).Select(g => (Change: g.Last().ChangeId, Record: Records[g.Key])).ToList();
        if (batch.Count == 0)
            return;
        var changes = batch.Select(b => b.Record.IsDeleted
            ? new SyncChangeDto(b.Change, SyncEntityTypes.CareEntry, b.Record.Id, SyncOperations.Delete, Dto(b.Record.Version))
            : new SyncChangeDto(b.Change, SyncEntityTypes.CareEntry, b.Record.Id, SyncOperations.Upsert, Dto(b.Record.Version),
                CareEntry: new CareEntryDto("diaper", Calls.Id(5000), null, false, SyncCalls.At, null,
                    "user:7a1c0000-0000-7000-8000-000000000001", "manual", null, b.Record.Note,
                    JsonDocument.Parse("""{"v":1,"contents":"wet"}""").RootElement.Clone()))).ToArray();
        var response = await SyncCalls.PushAsync(client, accessToken, household, device, changes);
        response.EnsureSuccessStatusCode();

        // A final result removes that record's rows up to the change it answered (ADR 0002 §2).
        var answered = (await response.Content.ReadFromJsonAsync<PushResponse>())!.Results.Select(r => r.ChangeId).ToHashSet();
        var cut = batch.Where(b => answered.Contains(b.Change))
            .ToDictionary(b => b.Record.Id, b => _outbox.FindLastIndex(o => o.ChangeId == b.Change));
        var kept = _outbox.Where((o, i) => !cut.TryGetValue(o.EntityId, out var last) || i > last).ToList();
        _outbox.Clear();
        _outbox.AddRange(kept);
    }

    public async Task PullAsync()
    {
        // A device that already holds records must see every tombstone, even on its first pull (ruling 20).
        if (_cursor == 0 && _head is null && Records.Count > 0)
            _head = 0;

        while (true)
        {
            var response = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, _cursor, _head), accessToken);
            response.EnsureSuccessStatusCode();
            var page = (await response.Content.ReadFromJsonAsync<PullResponse>())!;
            foreach (var record in page.Records)
            {
                var incoming = new HlcStamp(record.Version.WallClockMs, record.Version.Counter, new DeviceId(record.Version.DeviceId));
                _clock.Observe(incoming, _wall);
                var pending = _outbox.Any(o => o.EntityId == record.EntityId);
                if (pending && Records[record.EntityId].Version > incoming)
                    continue;
                Records[record.EntityId] = new Diaper(record.EntityId, incoming, record.IsDeleted, record.CareEntry?.Note);
            }

            (_cursor, _head) = (page.Cursor, page.BootstrapHead ?? _head);
            if (!page.HasMore)
                return;
        }
    }

    private void Write(Diaper diaper)
    {
        Records[diaper.Id] = diaper;
        _outbox.Add((Calls.Id(900_000 + number * 10_000 + ++_changes), diaper.Id));
    }

    private HlcStamp Tick() => _clock.Tick(_wall);

    private static HlcStampDto Dto(HlcStamp stamp) => new(stamp.WallClockMs, stamp.Counter, stamp.DeviceId.Value);
}
