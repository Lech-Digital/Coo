using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Infrastructure.Persistence;

namespace Coo.Mobile.Infrastructure.Tests;

public sealed class AdvisorThreadStoreTests
{
    [Fact]
    public async Task ListThreads_Empty_ReturnsEmptyList()
    {
        await using var db = await TestDatabase.CreateAsync();
        var store = new AdvisorThreadStore(db.Database);

        var threads = await store.ListThreadsAsync(CancellationToken.None);

        threads.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAndGetThread_RoundTripsMessages()
    {
        await using var db = await TestDatabase.CreateAsync();
        var store = new AdvisorThreadStore(db.Database);
        var now = db.Time.GetUtcNow();

        var thread = new AdvisorThread
        {
            Id = Guid.CreateVersion7(now).ToString("N"),
            Title = "Sleeping through the night",
            CreatedAt = now,
            LastUpdatedAt = now,
            Messages =
            [
                new AdvisorMessageItem
                {
                    Id = Guid.CreateVersion7(now).ToString("N"),
                    Text = "When do babies sleep longer?",
                    IsUser = true,
                    Timestamp = now
                },
                new AdvisorMessageItem
                {
                    Id = Guid.CreateVersion7(now).ToString("N"),
                    Text = "Most babies begin longer night stretches around 2-3 months.",
                    IsUser = false,
                    Timestamp = now
                }
            ]
        };

        await store.SaveThreadAsync(thread, CancellationToken.None);

        var list = await store.ListThreadsAsync(CancellationToken.None);
        list.Should().HaveCount(1);
        list[0].Id.Should().Be(thread.Id);
        list[0].Title.Should().Be(thread.Title);
        list[0].MessageCount.Should().Be(2);

        var retrieved = await store.GetThreadAsync(thread.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.Title.Should().Be("Sleeping through the night");
        retrieved.Messages.Should().HaveCount(2);
        retrieved.Messages[0].Text.Should().Be("When do babies sleep longer?");
        retrieved.Messages[1].Text.Should().Be("Most babies begin longer night stretches around 2-3 months.");
    }

    [Fact]
    public async Task DeleteThread_RemovesFromIndexAndDetail()
    {
        await using var db = await TestDatabase.CreateAsync();
        var store = new AdvisorThreadStore(db.Database);
        var now = db.Time.GetUtcNow();

        var thread = new AdvisorThread
        {
            Id = Guid.CreateVersion7(now).ToString("N"),
            Title = "Tummy time questions",
            CreatedAt = now,
            LastUpdatedAt = now,
            Messages = []
        };

        await store.SaveThreadAsync(thread, CancellationToken.None);
        (await store.ListThreadsAsync(CancellationToken.None)).Should().HaveCount(1);

        await store.DeleteThreadAsync(thread.Id, CancellationToken.None);

        (await store.ListThreadsAsync(CancellationToken.None)).Should().BeEmpty();
        (await store.GetThreadAsync(thread.Id, CancellationToken.None)).Should().BeNull();
    }
}
