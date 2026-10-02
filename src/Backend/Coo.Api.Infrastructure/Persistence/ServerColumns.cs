using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.Common;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// How Domain values become column values and back, on the server. Each parse is total: it returns null instead of
/// throwing, so a bad stored value is reported by the read guard rather than breaking a read (ADR 0010 G4).
/// The text forms match the phone's, so a row means the same on both sides.
/// </summary>
internal static class ServerColumns
{
    public const string ThisDevice = "this-device";
    private const string UserPrefix = "user:";

    public static string Person(PersonRef person) => person switch
    {
        UserPerson user => UserPrefix + user.UserId.Value.ToString("D", CultureInfo.InvariantCulture),
        ThisDevicesPersonRef => ThisDevice,
        _ => throw new ArgumentOutOfRangeException(nameof(person), person.GetType().Name, "Unknown person reference."),
    };

    public static PersonRef? ParsePerson(string? value)
    {
        if (value == ThisDevice)
            return PersonRef.ThisDevicesPerson;
        if (value is not null && value.StartsWith(UserPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value[UserPrefix.Length..], "D", out var id) && id != Guid.Empty)
            return PersonRef.User(new UserId(id));
        return null;
    }

    public static string Source(EntrySource source) => source.Kind switch
    {
        EntrySourceKind.Manual => "manual",
        EntrySourceKind.Timer => "timer",
        EntrySourceKind.Watch => "watch",
        EntrySourceKind.AiParsed => "ai-parsed",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown entry source."),
    };

    public static EntrySource? ParseSource(string? value, string? promptVersion) => value switch
    {
        "manual" => EntrySource.Manual,
        "timer" => EntrySource.Timer,
        "watch" => EntrySource.Watch,
        "ai-parsed" when !string.IsNullOrWhiteSpace(promptVersion) => EntrySource.AiParsed(promptVersion),
        _ => null,
    };

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
