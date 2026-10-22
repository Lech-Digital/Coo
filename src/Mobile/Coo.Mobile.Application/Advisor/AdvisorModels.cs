namespace Coo.Mobile.Application.Advisor;

public sealed class AdvisorThreadSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public int MessageCount { get; set; }
}

public sealed class AdvisorThread
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public List<AdvisorMessageItem> Messages { get; set; } = [];
}

public sealed class AdvisorMessageItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsUser { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The app's own message (a refusal, no connection): shown, never sent back to the Advisor as history.</summary>
    public bool IsError { get; set; }
}

public interface IAdvisorThreadStore
{
    Task<IReadOnlyList<AdvisorThreadSummary>> ListThreadsAsync(CancellationToken cancellationToken);
    Task<AdvisorThread?> GetThreadAsync(string id, CancellationToken cancellationToken);
    Task SaveThreadAsync(AdvisorThread thread, CancellationToken cancellationToken);
    Task DeleteThreadAsync(string id, CancellationToken cancellationToken);
}
