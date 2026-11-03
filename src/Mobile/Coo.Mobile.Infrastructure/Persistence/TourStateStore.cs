using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>"How Coo works" seen on this phone, in <c>app_state</c>; never synced.</summary>
internal sealed class TourStateStore(LocalDatabase database) : ITourState
{
    private const string Key = "tour.seen";

    public async Task<bool> HasSeenAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", Key)) is string;
    }

    public async Task MarkSeenAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, '1') ON CONFLICT (key) DO NOTHING;",
            cancellationToken, null, ("$k", Key));
    }
}
