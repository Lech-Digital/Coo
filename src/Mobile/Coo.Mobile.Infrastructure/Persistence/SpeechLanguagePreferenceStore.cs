using Coo.Mobile.Application.AiLogging;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>The language spoken to Tell Coo, in <c>app_state</c>; never synced. Empty is the app's language.</summary>
internal sealed class SpeechLanguagePreferenceStore(LocalDatabase database) : ISpeechLanguagePreference
{
    private const string Key = "quick.speech_language";

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var value = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", Key));
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public async Task SetAsync(string? language, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$k", Key), ("$v", language ?? ""));
    }
}
