using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.Common;

namespace Coo.Api.Application.Sync;

/// <summary>
/// The text forms of a person and a source, as the phone writes them and the server stores them (Spec 004 §4.5). Each
/// parse is total: null instead of throwing. <c>ServerColumns</c> delegates here, so there is one definition.
/// </summary>
public static class SyncText
{
    public const string ThisDevice = "this-device";
    private const string UserPrefix = "user:";

    public static string Person(PersonRef person) => person switch
    {
        UserPerson user => UserPrefix + user.UserId.Value.ToString("D", CultureInfo.InvariantCulture),
        ThisDevicesPersonRef => ThisDevice,
        _ => throw new ArgumentOutOfRangeException(nameof(person), person?.GetType().Name, "Unknown person reference."),
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

    public static string Source(EntrySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            EntrySourceKind.Manual => "manual",
            EntrySourceKind.Timer => "timer",
            EntrySourceKind.Watch => "watch",
            EntrySourceKind.AiParsed => "ai-parsed",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown entry source."),
        };
    }

    public static EntrySource? ParseSource(string? value, string? promptVersion) => value switch
    {
        "manual" => EntrySource.Manual,
        "timer" => EntrySource.Timer,
        "watch" => EntrySource.Watch,
        "ai-parsed" when !string.IsNullOrWhiteSpace(promptVersion) => EntrySource.AiParsed(promptVersion),
        _ => null,
    };
}
