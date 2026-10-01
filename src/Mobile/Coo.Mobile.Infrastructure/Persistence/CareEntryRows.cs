using Coo.Codecs;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>One <c>care_entries</c> row as read, before it is classified.</summary>
internal sealed record CareEntryRow(
    string Id, string HouseholdId, string Kind, string? SubjectBabyId, string? SubjectPerson, bool IsSession,
    long StartUtcMs, long StartOffsetMin, long? EndUtcMs, long? EndOffsetMin, string LoggedBy, string Source,
    string? SourcePromptVersion, string? Note, string Details, long VersionWallMs, long VersionCounter,
    string VersionDevice, bool IsDeleted, string Integrity)
{
    public const string SelectColumns =
        """
        id, household_id, kind, subject_baby_id, subject_person, is_session, start_utc_ms, start_offset_min,
        end_utc_ms, end_offset_min, logged_by, source, source_prompt_version, note, details,
        version_wall_ms, version_counter, version_device, is_deleted, integrity
        """;

    public static CareEntryRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), NullableString(r, 3), NullableString(r, 4), r.GetInt64(5) == 1,
        r.GetInt64(6), r.GetInt64(7), NullableLong(r, 8), NullableLong(r, 9), r.GetString(10), r.GetString(11),
        NullableString(r, 12), NullableString(r, 13), r.GetString(14), r.GetInt64(15), r.GetInt64(16), r.GetString(17),
        r.GetInt64(18) == 1, r.GetString(19));

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}

/// <summary>A row's integrity, as the read guard sees it (Spec 004 §6).</summary>
internal sealed record Classified(StoredEntry Entry, string Integrity, string? Reason);

/// <summary>
/// The read guard (G4): turns a row into a live, read-only or quarantined entry. It never throws for a bad
/// row, and never changes the row's bytes.
/// </summary>
internal static class CareEntryClassifier
{
    public const string Ok = "ok";
    public const string ReadOnly = "read-only";
    public const string Quarantined = "quarantined";

    public static Classified Classify(CareEntryRow row)
    {
        var id = Columns.ParseGuid(row.Id) is { } guid ? new EntryId(guid) : (EntryId?)null;
        var start = Columns.ParseInstant(row.StartUtcMs, row.StartOffsetMin);
        if (id is null || start is null)
            return Quarantine(id ?? default, row, start ?? DateTimeOffset.UnixEpoch, "core.invalid:id-or-start");

        var end = row.EndUtcMs is { } endMs && row.EndOffsetMin is { } endOffset ? Columns.ParseInstant(endMs, endOffset) : null;
        var loggedBy = Columns.ParsePerson(row.LoggedBy);

        if (EntryKindCode.From(row.Kind) is not { IsSuccess: true } code || EntryKinds.Find(code.Value) is not { } kind
            || EntryDetailsCodecs.For(code.Value) is not { } codec)
            return new(new ReadOnlyEntry(id.Value, row.Kind, start.Value, end, loggedBy, EntryErrors.KindUnknown), ReadOnly, EntryErrors.KindUnknown);

        switch (codec.Decode(row.Details))
        {
            case Unrepresentable unrepresentable:
                return new(new ReadOnlyEntry(id.Value, row.Kind, start.Value, end, loggedBy, unrepresentable.Reason), ReadOnly, unrepresentable.Reason);
            case Malformed malformed:
                return Quarantine(id.Value, row, start.Value, malformed.Reason);
            case Decoded decoded:
                return Rebuild(id.Value, row, kind, decoded.Details, start.Value, end, loggedBy);
            default:
                throw new InvalidOperationException("Unknown decode outcome.");
        }
    }

    private static Classified Rebuild(
        EntryId id, CareEntryRow row, IEntryKind kind, EntryDetails details, DateTimeOffset start, DateTimeOffset? end, PersonRef? loggedBy)
    {
        var household = Columns.ParseGuid(row.HouseholdId);
        EntrySubject? subject = row switch
        {
            { SubjectBabyId: { } baby } => Columns.ParseGuid(baby) is { } babyId ? EntrySubject.Baby(new BabyId(babyId)) : null,
            { SubjectPerson: { } person } => Columns.ParsePerson(person) is { } p ? EntrySubject.Caregiver(p) : null,
            _ => null,
        };
        var source = Columns.ParseSource(row.Source, row.SourcePromptVersion);
        var version = Columns.ParseStamp(row.VersionWallMs, row.VersionCounter, row.VersionDevice);
        var note = Note.From(row.Note);
        EntryTiming? timing = row.IsSession
            ? TimeRange.Create(start, end) is { IsSuccess: true } range ? EntryTiming.Session(range.Value) : null
            : end is null ? EntryTiming.Point(start) : null;

        if (household is null || subject is null || loggedBy is null || source is null || version is null || note.IsFailure || timing is null)
            return Quarantine(id, row, start, "core.invalid");

        var entry = CareEntry.Rehydrate(
            id, new HouseholdId(household.Value), kind, subject, timing, details, loggedBy, source, note.Value, version.Value, row.IsDeleted);
        return new(new LiveEntry(entry), Ok, null);
    }

    private static Classified Quarantine(EntryId id, CareEntryRow row, DateTimeOffset start, string reason) =>
        new(new QuarantinedEntry(id, row.Kind, start, reason), Quarantined, reason);
}
