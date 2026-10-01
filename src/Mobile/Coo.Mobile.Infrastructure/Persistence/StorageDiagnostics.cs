using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Writes deliberately bad rows with raw SQL, for the debug screen only (Spec 004 §8).</summary>
internal sealed class StorageDiagnostics(LocalDatabase database, TimeProvider time) : IStorageDiagnostics
{
    public string DatabasePath => database.Path;

    /// <summary>A breast feed with no segments: valid JSON with a version, so the database accepts it, but malformed.</summary>
    public Task InsertMalformedRowAsync(HouseholdId household, BabyId baby, CancellationToken cancellationToken) =>
        InsertAsync(household, baby, "feeding", """{"v":1,"method":"breast","completed":true,"segments":[]}""", cancellationToken);

    /// <summary>A kind this build has never heard of, as a newer app would write it.</summary>
    public Task InsertForeignKindRowAsync(HouseholdId household, BabyId baby, CancellationToken cancellationToken) =>
        InsertAsync(household, baby, "medication", """{"v":1,"drug":"vitamin D","doseDrops":1}""", cancellationToken);

    private async Task InsertAsync(HouseholdId household, BabyId baby, string kind, string details, CancellationToken cancellationToken)
    {
        var now = time.GetLocalNow();
        var (startMs, offset) = Columns.Instant(now);
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min,
                logged_by, source, details, version_wall_ms, version_counter, version_device)
            VALUES ($id, $household, $kind, $baby, 0, $start, $offset, 'this-device', 'manual', $details, $start, 0, $device);
            """,
            cancellationToken,
            null,
            ("$id", Columns.Id(Guid.CreateVersion7(now))), ("$household", Columns.Id(household.Value)), ("$kind", kind),
            ("$baby", Columns.Id(baby.Value)), ("$start", startMs), ("$offset", offset), ("$details", details),
            ("$device", Columns.Id(Guid.CreateVersion7(now))));
    }
}
