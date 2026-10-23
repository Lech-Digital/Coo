using System.Globalization;
using Coo.Mobile.Application.Notifications;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>The reminders turned on on this phone, in <c>app_state</c>; never synced (ADR 0007 §4). Unreadable values are off.</summary>
internal sealed class ReminderSettingsStore(LocalDatabase database) : IReminderSettingsStore
{
    private const string FeedEveryKey = "reminders.feed_every_minutes";
    private const string FeedAtNightKey = "reminders.feed_at_night";
    private const string TimerKey = "reminders.timer";
    private const string NapKey = "reminders.nap";

    public async Task<ReminderSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        async Task<string?> Read(string key) =>
            (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", key));

        var minutes = int.TryParse(await Read(FeedEveryKey), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : (int?)null;
        return new ReminderSettings(
            minutes is { } every ? TimeSpan.FromMinutes(every) : null,
            await Read(FeedAtNightKey) == "1",
            await Read(TimerKey) == "1",
            await Read(NapKey) == "1");
    }

    public async Task SetAsync(ReminderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (key, value) in new[]
                 {
                     (FeedEveryKey, settings.FeedEvery is { } every ? ((int)every.TotalMinutes).ToString(CultureInfo.InvariantCulture) : ""),
                     (FeedAtNightKey, settings.FeedAtNight ? "1" : "0"),
                     (TimerKey, settings.TimerRunning ? "1" : "0"),
                     (NapKey, settings.NapWindow ? "1" : "0"),
                 })
        {
            await connection.ExecuteAsync(
                "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                cancellationToken, transaction, ("$k", key), ("$v", value));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
