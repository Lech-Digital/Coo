using Coo.Mobile.Application.Insights;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>The Advisor's plan words in <c>app_state</c>, one row per facts' fingerprint (Spec 019 §4).</summary>
internal sealed class SleepPlanWordsStore(LocalDatabase database) : ISleepPlanWordsStore
{
    private const string Prefix = "sleepplan.words.";

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await database.OpenAsync(cancellationToken);
        return (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", Prefix + key));
    }

    public async Task SaveAsync(string key, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(text);
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$k", Prefix + key), ("$v", text));
    }
}
