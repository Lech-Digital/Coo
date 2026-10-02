using System.Collections.Concurrent;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>The secret store, in memory: an outbound port. Concurrent, because the handler tests call it from ten requests.</summary>
internal sealed class MemorySecrets : ISecretStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>The API, as far as an HttpClient can tell.</summary>
internal sealed class FakeServer(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return respond(request);
    }
}
