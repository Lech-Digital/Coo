using Coo.Domain.CareLog;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// The one upsert of a care entry (Spec 008 §4), shared by the store and sync. An existing ID in another household is
/// refused, never moved, and a stored version at least as high is kept (the WHERE, compared as a tuple like
/// <c>HlcStamp</c>): no row affected means the write did not land. Immutable columns (kind, subject, who logged it,
/// source) are never updated.
/// </summary>
internal static class CareEntryWriter
{
    public const string UpsertSql =
        """
        INSERT INTO care_entries (
            id, household_id, kind, subject_baby_id, subject_person, is_session, start_at, start_offset_min,
            end_at, end_offset_min, logged_by, source, source_prompt_version, note, details,
            version_wall_ms, version_counter, version_device, is_deleted, seq)
        VALUES (
            @id, @household_id, @kind, @subject_baby_id, @subject_person, @is_session, @start_at, @start_offset_min,
            @end_at, @end_offset_min, @logged_by, @source, @source_prompt_version, @note, @details,
            @version_wall_ms, @version_counter, @version_device, @is_deleted, @seq)
        ON CONFLICT (id) DO UPDATE SET
            is_session = excluded.is_session, start_at = excluded.start_at, start_offset_min = excluded.start_offset_min,
            end_at = excluded.end_at, end_offset_min = excluded.end_offset_min, note = excluded.note,
            details = excluded.details, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
            version_device = excluded.version_device, is_deleted = excluded.is_deleted, seq = excluded.seq
        WHERE care_entries.household_id = excluded.household_id
            AND (care_entries.version_wall_ms, care_entries.version_counter, care_entries.version_device)
                < (excluded.version_wall_ms, excluded.version_counter, excluded.version_device);
        """;

    public static void AddParameters(NpgsqlCommand command, CareEntry entry, string details, long seq)
    {
        var (start, end) = entry.Timing switch
        {
            PointTiming point => (point.At, (DateTimeOffset?)null),
            SessionTiming session => (session.Range.Start, session.Range.End),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Timing.GetType().Name, "Unknown timing."),
        };
        var (startUtc, startOffset) = ServerColumns.Instant(start);
        var endParts = end is { } e ? ServerColumns.Instant(e) : ((DateTime, short)?)null;
        var p = command.Parameters;

        p.AddWithValue("id", entry.Id.Value);
        p.AddWithValue("household_id", entry.HouseholdId.Value);
        p.AddWithValue("kind", entry.KindCode.Value);
        p.AddWithValue("subject_baby_id", NpgsqlDbType.Uuid, entry.Subject is BabySubject baby ? baby.BabyId.Value : DBNull.Value);
        p.AddWithValue("subject_person", NpgsqlDbType.Text, entry.Subject is CaregiverSubject caregiver ? ServerColumns.Person(caregiver.Person) : DBNull.Value);
        p.AddWithValue("is_session", entry.Timing is SessionTiming);
        p.AddWithValue("start_at", NpgsqlDbType.TimestampTz, startUtc);
        p.AddWithValue("start_offset_min", startOffset);
        p.AddWithValue("end_at", NpgsqlDbType.TimestampTz, endParts is { } ep ? ep.Item1 : DBNull.Value);
        p.AddWithValue("end_offset_min", NpgsqlDbType.Smallint, endParts is { } eo ? eo.Item2 : DBNull.Value);
        p.AddWithValue("logged_by", ServerColumns.Person(entry.LoggedBy));
        p.AddWithValue("source", ServerColumns.Source(entry.Source));
        p.AddWithValue("source_prompt_version", NpgsqlDbType.Text, (object?)entry.Source.PromptVersion ?? DBNull.Value);
        p.AddWithValue("note", NpgsqlDbType.Text, (object?)entry.Note?.Text ?? DBNull.Value);
        p.AddWithValue("details", NpgsqlDbType.Jsonb, details);
        p.AddWithValue("version_wall_ms", entry.Version.WallClockMs);
        p.AddWithValue("version_counter", entry.Version.Counter);
        p.AddWithValue("version_device", entry.Version.DeviceId.Value);
        p.AddWithValue("is_deleted", entry.IsDeleted);
        p.AddWithValue("seq", seq);
    }
}
