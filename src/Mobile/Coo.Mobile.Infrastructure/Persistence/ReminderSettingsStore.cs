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
    private const string NapLeadKey = "reminders.nap_lead_minutes";
    private const string DiaperEveryKey = "reminders.diaper_every_minutes";
    private const string DiaperAtNightKey = "reminders.diaper_at_night";
    private const string BathEveryKey = "reminders.bath_every_days";
    private const string BathAtKey = "reminders.bath_at";
    private const string MedicinesKey = "reminders.medicines";

    public async Task<ReminderSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        async Task<string?> Read(string key) =>
            (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", key));

        var minutes = int.TryParse(await Read(FeedEveryKey), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : (int?)null;
        var lead = int.TryParse(await Read(NapLeadKey), NumberStyles.None, CultureInfo.InvariantCulture, out var l) && ReminderSettings.NapLeads.Contains(l)
            ? l
            : ReminderSettings.Off.NapLeadMinutes;
        var diaper = int.TryParse(await Read(DiaperEveryKey), NumberStyles.None, CultureInfo.InvariantCulture, out var dm) && dm > 0 ? dm : (int?)null;
        var bathDays = int.TryParse(await Read(BathEveryKey), NumberStyles.None, CultureInfo.InvariantCulture, out var bd) && ReminderSettings.BathIntervals.Contains(bd)
            ? bd
            : (int?)null;
        var bathAt = TimeOnly.TryParseExact(await Read(BathAtKey), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ba) ? ba : (TimeOnly?)null;
        return new ReminderSettings(
            minutes is { } every ? TimeSpan.FromMinutes(every) : null,
            await Read(FeedAtNightKey) == "1",
            await Read(TimerKey) == "1",
            await Read(NapKey) == "1",
            lead,
            diaper is { } d ? TimeSpan.FromMinutes(d) : null,
            await Read(DiaperAtNightKey) == "1",
            bathDays,
            bathAt,
            ReadMedicines(await Read(MedicinesKey)));
    }

    // One line per medicine: name, dose, times (HH:mm, comma-separated), last day (yyyy-MM-dd or empty), tab-separated.
    internal static IReadOnlyList<MedicineReminder> ReadMedicines(string? text)
    {
        var list = new List<MedicineReminder>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 4 || string.IsNullOrWhiteSpace(parts[0]))
                continue;
            var times = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : (TimeOnly?)null)
                .OfType<TimeOnly>()
                .ToList();
            var until = DateOnly.TryParseExact(parts[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u) ? u : (DateOnly?)null;
            if (times.Count > 0)
                list.Add(new MedicineReminder(parts[0], parts[1].Length == 0 ? null : parts[1], times, until));
        }

        return list;
    }

    internal static string WriteMedicines(IReadOnlyList<MedicineReminder> medicines)
    {
        static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Trim();
        return string.Join('\n', medicines.Select(m => string.Join('\t',
            Clean(m.Name),
            Clean(m.Dose),
            string.Join(',', m.Times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture))),
            m.Until?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "")));
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
                     (NapLeadKey, settings.NapLeadMinutes.ToString(CultureInfo.InvariantCulture)),
                     (DiaperEveryKey, settings.DiaperEvery is { } diaper ? ((int)diaper.TotalMinutes).ToString(CultureInfo.InvariantCulture) : ""),
                     (DiaperAtNightKey, settings.DiaperAtNight ? "1" : "0"),
                     (BathEveryKey, settings.BathEveryDays?.ToString(CultureInfo.InvariantCulture) ?? ""),
                     (BathAtKey, settings.BathAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? ""),
                     (MedicinesKey, WriteMedicines(settings.MedicineList)),
                 })
        {
            await connection.ExecuteAsync(
                "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                cancellationToken, transaction, ("$k", key), ("$v", value));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
