using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The session store in memory: what <see cref="Account"/> saves is what the use cases read.</summary>
internal sealed class MemorySessions : ISessionStore
{
    public StoredSession? Stored { get; private set; }

    public Task<StoredSession?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
    {
        Stored = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Stored = null;
        return Task.CompletedTask;
    }
}
