using Coo.Api.Application.CareLog;
using Coo.Codecs;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// The read guard (G4), server side: a row becomes a live entry, or an unreadable one with a reason. It never throws
/// for a bad row, and never changes the row. The server knows every kind, so anything that does not decode, including
/// a version newer than this build, is a failure the integrity scan reports.
/// </summary>
internal static class ServerReadGuard
{
    public static ServerStoredEntry Classify(CareEntryRecord row)
    {
        var id = new EntryId(row.Id);
        if (EntryKindCode.From(row.Kind) is not { IsSuccess: true } code || EntryKinds.Find(code.Value) is not { } kind
            || EntryDetailsCodecs.For(code.Value) is not { } codec)
            return new UnreadableServerEntry(id, row.Kind, EntryErrors.KindUnknown);

        return codec.Decode(row.Details) switch
        {
            Decoded decoded => Rebuild(row, kind, decoded.Details),
            Unrepresentable unrepresentable => new UnreadableServerEntry(id, row.Kind, unrepresentable.Reason),
            Malformed malformed => new UnreadableServerEntry(id, row.Kind, malformed.Reason),
            _ => throw new InvalidOperationException("Unknown decode outcome."),
        };
    }

    private static ServerStoredEntry Rebuild(CareEntryRecord row, IEntryKind kind, EntryDetails details)
    {
        var id = new EntryId(row.Id);
        var start = ServerColumns.ParseInstant(row.StartAt, row.StartOffsetMin);
        var end = row is { EndAt: { } endAt, EndOffsetMin: { } endOffset } ? ServerColumns.ParseInstant(endAt, endOffset) : null;
        EntrySubject? subject = row switch
        {
            { SubjectBabyId: { } baby } when baby != Guid.Empty => EntrySubject.Baby(new BabyId(baby)),
            { SubjectPerson: { } person } => ServerColumns.ParsePerson(person) is { } p ? EntrySubject.Caregiver(p) : null,
            _ => null,
        };
        var loggedBy = ServerColumns.ParsePerson(row.LoggedBy);
        var source = ServerColumns.ParseSource(row.Source, row.SourcePromptVersion);
        var note = Note.From(row.Note);
        EntryTiming? timing = start is null ? null
            : row.IsSession
                ? TimeRange.Create(start.Value, end) is { IsSuccess: true } range ? EntryTiming.Session(range.Value) : null
                : end is null ? EntryTiming.Point(start.Value) : null;

        if (subject is null || loggedBy is null || source is null || note.IsFailure || timing is null
            || row.VersionCounter < 0 || row.VersionDevice == Guid.Empty || row.HouseholdId == Guid.Empty)
            return new UnreadableServerEntry(id, row.Kind, "core.invalid");

        var version = new HlcStamp(row.VersionWallMs, row.VersionCounter, new DeviceId(row.VersionDevice));
        var entry = CareEntry.Rehydrate(
            id, new HouseholdId(row.HouseholdId), kind, subject, timing, details, loggedBy, source, note.Value, version, row.IsDeleted);
        return new LiveServerEntry(entry, row.Seq);
    }
}
