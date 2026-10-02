using Coo.Api.Application.Sync;
using Coo.Domain.CareLog;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// How Domain values become column values and back, on the server. Each parse is total: it returns null instead of
/// throwing, so a bad stored value is reported by the read guard rather than breaking a read (ADR 0010 G4).
/// The person and source text forms are <see cref="SyncText"/>'s, the phone's, so a row means the same on both sides.
/// </summary>
internal static class ServerColumns
{
    public const string ThisDevice = SyncText.ThisDevice;

    public static string Person(PersonRef person) => SyncText.Person(person);

    public static PersonRef? ParsePerson(string? value) => SyncText.ParsePerson(value);

    public static string Source(EntrySource source) => SyncText.Source(source);

    public static EntrySource? ParseSource(string? value, string? promptVersion) => SyncText.ParseSource(value, promptVersion);

    /// <summary>The instant in UTC for <c>timestamptz</c>, and the offset the person saw, kept beside it.</summary>
    public static (DateTime Utc, short OffsetMin) Instant(DateTimeOffset value) =>
        (value.UtcDateTime, (short)value.Offset.TotalMinutes);

    public static DateTimeOffset? ParseInstant(DateTime utc, int offsetMin)
    {
        if (offsetMin is < -14 * 60 or > 14 * 60)
            return null;
        return Utc(utc).ToOffset(TimeSpan.FromMinutes(offsetMin));
    }

    /// <summary>A <c>timestamptz</c> read back as the UTC instant it is, whatever kind the reader gave the value.</summary>
    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
