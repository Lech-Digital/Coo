using System.Globalization;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Per-device settings in <c>app_state</c>; never synced (Spec 005 §6.3). Missing or unreadable values fall back to the defaults.</summary>
internal sealed class DevicePreferences(LocalDatabase database) : IDevicePreferences
{
    private const string ModeKey = "appearance.mode";
    private const string QuietStartKey = "appearance.quiet_start";
    private const string QuietEndKey = "appearance.quiet_end";

    public async Task<AppearancePreferences> GetAppearanceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var mode = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", ModeKey));
        var start = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", QuietStartKey));
        var end = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", QuietEndKey));

        var defaults = AppearancePreferences.Default;
        return new AppearancePreferences(
            Enum.TryParse<AppearanceMode>(mode, out var m) && Enum.IsDefined(m) ? m : defaults.Mode,
            new QuietHours(ParseTime(start) ?? defaults.QuietHours.Start, ParseTime(end) ?? defaults.QuietHours.End));
    }

    public async Task SetAppearanceAsync(AppearancePreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (key, value) in new[]
                 {
                     (ModeKey, preferences.Mode.ToString()),
                     (QuietStartKey, preferences.QuietHours.Start.ToString("HH:mm", CultureInfo.InvariantCulture)),
                     (QuietEndKey, preferences.QuietHours.End.ToString("HH:mm", CultureInfo.InvariantCulture)),
                 })
        {
            await connection.ExecuteAsync(
                "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                cancellationToken, transaction, ("$k", key), ("$v", value));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
