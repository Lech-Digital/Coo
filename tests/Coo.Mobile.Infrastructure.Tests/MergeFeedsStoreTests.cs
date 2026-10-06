using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Infrastructure.Persistence;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011d §7: a merge and its undo on the real store — one feed, outbox rows, then three feeds again.</summary>
public sealed class MergeFeedsStoreTests
{
    [Fact]
    public async Task MergeThenUndo_ThreeBreastFeeds_ShowsOneFeedThenThreeWithOutboxRowsForEach()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var t0 = db.Time.GetLocalNow().AddHours(-2);
        var a = await BreastAsync(db, t0, 10, "fussy");
        var b = await BreastAsync(db, t0.AddMinutes(12), 5, null);
        var c = await BreastAsync(db, t0.AddMinutes(19), 4, "slept after");
        await db.ExecuteAsync("DELETE FROM outbox;");
        var sync = new SyncStore(db.Database);

        // Act
        var result = await new MergeFeeds(db.Store, sync, db.Device, db.Time).ExecuteAsync([a.Id, b.Id, c.Id], CancellationToken.None);
        var afterMerge = await ListAsync(db, t0);
        var outboxAfterMerge = await OutboxAsync(db);
        await new UndoFeedMerge(db.Store, db.Device, db.Time).ExecuteAsync(result.Merged!, CancellationToken.None);
        var afterUndo = await ListAsync(db, t0);

        // Assert
        result.MergedId.Should().Be(a.Id);
        var merged = afterMerge.Should().ContainSingle().Which;
        ((BreastFeed)merged.Details).Segments.Should().HaveCount(3);
        merged.Note!.Text.Should().Be("fussy · slept after");
        outboxAfterMerge.Should().BeEquivalentTo([$"{a.Id}:upsert", $"{b.Id}:delete", $"{c.Id}:delete"]);

        afterUndo.Select(e => e.Id).Should().BeEquivalentTo([a.Id, b.Id, c.Id]);
        var restored = afterUndo.Single(e => e.Id == a.Id);
        ((BreastFeed)restored.Details).Segments.Should().ContainSingle();
        restored.Note!.Text.Should().Be("fussy");
        restored.Version.Should().BeGreaterThan(merged.Version);
        afterUndo.Single(e => e.Id == b.Id).Version.Should().BeGreaterThan(merged.Version, "a restore wins over the tombstone");
    }

    private static async Task<CareEntry> BreastAsync(TestDatabase db, DateTimeOffset start, int minutes, string? note)
    {
        var details = BreastFeed.Create([new BreastSegment(BreastSide.Left, start, start.AddMinutes(minutes))], isCompleted: true).Value;
        var entry = CareEntry.Create(await db.NewIdAsync(), TestDatabase.Household, EntrySubject.Baby(TestDatabase.Baby), details, null,
            PersonRef.User(TestDatabase.Parent), EntrySource.Manual, await db.Device.NextAsync(CancellationToken.None), db.Time.GetUtcNow(),
            Note.From(note).Value).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);
        return entry;
    }

    private static async Task<List<CareEntry>> ListAsync(TestDatabase db, DateTimeOffset t0) =>
        [.. (await db.Store.ListAsync(TestDatabase.Household, t0.AddHours(-1), t0.AddHours(1), CancellationToken.None)).Cast<LiveEntry>().Select(l => l.Entry)];

    private static async Task<List<string>> OutboxAsync(TestDatabase db)
    {
        await using var connection = await db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_id, operation FROM outbox;";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        return rows;
    }
}
