using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// How Domain values become column values and back. Each parse is total: it returns null instead of throwing,
/// so a bad stored value quarantines its row rather than breaking a read (Spec 004 §6).
/// </summary>
internal static class Columns
{
    public const string ThisDevice = "this-device";
    private const string UserPrefix = "user:";

    public static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    public static Guid? ParseGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : null;

    public static string Person(PersonRef person) => person switch
    {
        UserPerson user => UserPrefix + Id(user.UserId.Value),
        ThisDevicesPersonRef => ThisDevice,
        _ => throw new ArgumentOutOfRangeException(nameof(person), person.GetType().Name, "Unknown person reference."),
    };

    public static PersonRef? ParsePerson(string? value)
    {
        if (value == ThisDevice)
            return PersonRef.ThisDevicesPerson;
        if (value is not null && value.StartsWith(UserPrefix, StringComparison.Ordinal) && ParseGuid(value[UserPrefix.Length..]) is { } id)
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

    /// <summary>UTC milliseconds sort correctly; the offset is kept beside them (Spec 004 §4.5).</summary>
    public static (long UtcMs, int OffsetMin) Instant(DateTimeOffset value) =>
        (value.ToUnixTimeMilliseconds(), (int)value.Offset.TotalMinutes);

    public static DateTimeOffset? ParseInstant(long utcMs, long offsetMin)
    {
        if (offsetMin is < -14 * 60 or > 14 * 60)
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToOffset(TimeSpan.FromMinutes(offsetMin));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static HlcStamp? ParseStamp(long wallMs, long counter, string? device) =>
        ParseGuid(device) is { } id && counter is >= 0 and <= int.MaxValue ? new HlcStamp(wallMs, (int)counter, new DeviceId(id)) : null;

    public static (string Name, object? Value)[] Stamp(HlcStamp version) =>
    [
        ("$version_wall_ms", version.WallClockMs),
        ("$version_counter", version.Counter),
        ("$version_device", Id(version.DeviceId.Value)),
    ];
}
