using System.Globalization;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Per-device settings in <c>app_state</c>, appearance and language; never synced (Spec 005 §6.3, Spec 009 §3.5). Missing or unreadable values fall back to the defaults.</summary>
internal sealed class DevicePreferences(LocalDatabase database) : IDevicePreferences
{
    private const string ModeKey = "appearance.mode";
    private const string QuietStartKey = "appearance.quiet_start";
    private const string QuietEndKey = "appearance.quiet_end";
    private const string OverridePaletteKey = "appearance.override_palette";
    private const string OverrideUntilKey = "appearance.override_until";
    private const string LanguageKey = "language";
    private const string SystemLanguage = "system";

    public async Task<AppearancePreferences> GetAppearanceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var mode = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", ModeKey));
        var start = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", QuietStartKey));
        var end = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", QuietEndKey));
        var overridePalette = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", OverridePaletteKey));
        var overrideUntil = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", OverrideUntilKey));

        var defaults = AppearancePreferences.Default;
        return new AppearancePreferences(
            Enum.TryParse<AppearanceMode>(mode, out var m) && Enum.IsDefined(m) ? m : defaults.Mode,
            new QuietHours(ParseTime(start) ?? defaults.QuietHours.Start, ParseTime(end) ?? defaults.QuietHours.End),
            ParseOverride(overridePalette, overrideUntil));
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
                     (OverridePaletteKey, preferences.Override?.Palette.ToString() ?? ""),
                     (OverrideUntilKey, preferences.Override?.Until.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                 })
        {
            await connection.ExecuteAsync(
                "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                cancellationToken, transaction, ("$k", key), ("$v", value));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LanguagePreference> GetLanguageAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var value = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", LanguageKey));
        return value is { Length: 2 } && value.All(char.IsAsciiLetterLower) ? new LanguagePreference(value) : LanguagePreference.System;
    }

    public async Task SetLanguageAsync(LanguagePreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preference);
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$k", LanguageKey), ("$v", preference.Language ?? SystemLanguage));
    }

    public async Task<DateTimeOffset?> GetHandoverSeenAsync(Guid person, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var value = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", $"handover_seen:{person:D}"));
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
    }

    public async Task SetHandoverSeenAsync(Guid person, DateTimeOffset lastSeen, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$k", $"handover_seen:{person:D}"),
            ("$v", lastSeen.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public async Task<string?> GetBabyAvatarAsync(Guid babyId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var key = $"baby_avatar:{babyId:D}";
        return (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", key));
    }

    public async Task SetBabyAvatarAsync(Guid babyId, string? avatarRef, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var key = $"baby_avatar:{babyId:D}";
        if (string.IsNullOrEmpty(avatarRef))
        {
            await connection.ExecuteAsync("DELETE FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", key));
        }
        else
        {
            await connection.ExecuteAsync(
                "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                cancellationToken, null, ("$k", key), ("$v", avatarRef));
        }
    }

    /// <summary>Only a whole, readable override counts; anything else means none.</summary>
    private static AppearanceOverride? ParseOverride(string? palette, string? until) =>
        Enum.TryParse<AppearanceMode>(palette, out var p) && p is AppearanceMode.Day or AppearanceMode.Night
        && DateTimeOffset.TryParseExact(until, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u)
            ? new AppearanceOverride(p, u)
            : null;

    private static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
