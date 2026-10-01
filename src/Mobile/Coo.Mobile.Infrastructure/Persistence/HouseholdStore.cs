using System.Globalization;
using System.Text.Json;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Households and babies in SQLite. Every write appends its outbox row in the same transaction.</summary>
internal sealed class HouseholdStore(LocalDatabase database, TimeProvider time) : IHouseholdStore
{
    public async Task<IReadOnlyList<HouseholdSettings>> ListHouseholdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.Command(
            """
            SELECT household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted
            FROM household_settings ORDER BY household_id;
            """);
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<HouseholdSettings>();
        while (await r.ReadAsync(cancellationToken))
        {
            var hidden = JsonSerializer.Deserialize(r.GetString(3), StringListJson.Default.ListString) ?? [];
            list.Add(HouseholdSettings.Rehydrate(
                new HouseholdId(Guid.Parse(r.GetString(0))),
                r.GetString(1) == "oz" ? VolumeUnit.Ounces : VolumeUnit.Millilitres,
                r.GetString(2),
                hidden.Select(EntryKindCode.From).Where(c => c.IsSuccess).Select(c => c.Value),
                Columns.ParseStamp(r.GetInt64(4), r.GetInt64(5), r.GetString(6))!.Value,
                r.GetInt64(7) == 1));
        }

        return list;
    }

    public async Task SaveSettingsAsync(HouseholdSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted)
            VALUES ($id, $unit, $tz, $hidden, $version_wall_ms, $version_counter, $version_device, $deleted)
            ON CONFLICT (household_id) DO UPDATE SET volume_unit = excluded.volume_unit, time_zone_id = excluded.time_zone_id,
                hidden_kinds = excluded.hidden_kinds, version_wall_ms = excluded.version_wall_ms,
                version_counter = excluded.version_counter, version_device = excluded.version_device, is_deleted = excluded.is_deleted;
            """,
            cancellationToken,
            transaction,
            [
                ("$id", Columns.Id(settings.Id.Value)),
                ("$unit", settings.VolumeUnit == VolumeUnit.Ounces ? "oz" : "ml"),
                ("$tz", settings.TimeZoneId),
                ("$hidden", JsonSerializer.Serialize(settings.HiddenKinds.Select(k => k.Value).Order(StringComparer.Ordinal).ToList(), StringListJson.Default.ListString)),
                .. Columns.Stamp(settings.Version),
                ("$deleted", settings.IsDeleted ? 1 : 0),
            ]);
        await Outbox.AppendAsync(connection, transaction, "HouseholdSettings", settings.Id.Value, settings.IsDeleted, settings.Version, time, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BabyProfile>> ListBabiesAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.Command(
            """
            SELECT id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted
            FROM baby_profiles WHERE household_id = $household AND is_deleted = 0 ORDER BY birth_date, name;
            """,
            null,
            ("$household", Columns.Id(household.Value)));
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<BabyProfile>();
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(BabyProfile.Rehydrate(
                new BabyId(Guid.Parse(r.GetString(0))),
                new HouseholdId(Guid.Parse(r.GetString(1))),
                r.GetString(2),
                JsonSerializer.Deserialize(r.GetString(3), StringListJson.Default.ListString) ?? [],
                DateOnly.ParseExact(r.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.IsDBNull(5) ? null : DateOnly.ParseExact(r.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Columns.ParseStamp(r.GetInt64(6), r.GetInt64(7), r.GetString(8))!.Value,
                r.GetInt64(9) == 1));
        }

        return list;
    }

    public async Task SaveBabyAsync(BabyProfile baby, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted)
            VALUES ($id, $household, $name, $nicknames, $birth, $due, $version_wall_ms, $version_counter, $version_device, $deleted)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, nicknames = excluded.nicknames, birth_date = excluded.birth_date,
                due_date = excluded.due_date, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                version_device = excluded.version_device, is_deleted = excluded.is_deleted;
            """,
            cancellationToken,
            transaction,
            [
                ("$id", Columns.Id(baby.Id.Value)),
                ("$household", Columns.Id(baby.HouseholdId.Value)),
                ("$name", baby.Name),
                ("$nicknames", JsonSerializer.Serialize(baby.Nicknames.ToList(), StringListJson.Default.ListString)),
                ("$birth", baby.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$due", baby.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                .. Columns.Stamp(baby.Version),
                ("$deleted", baby.IsDeleted ? 1 : 0),
            ]);
        await Outbox.AppendAsync(connection, transaction, "BabyProfile", baby.Id.Value, baby.IsDeleted, baby.Version, time, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal sealed partial class StringListJson : System.Text.Json.Serialization.JsonSerializerContext;
