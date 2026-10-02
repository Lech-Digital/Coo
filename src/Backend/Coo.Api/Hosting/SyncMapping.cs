using System.Text.Json;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.Hosting;

/// <summary>
/// Contracts to the use cases and back (the anti-corruption layer's edge, ADR 0002 §11). Details become a string here and
/// a <see cref="JsonElement"/> again on the way out; the Application layer never sees JSON types.
/// </summary>
internal static class SyncMapping
{
    /// <summary>
    /// A change the server can answer about: it has an ID and a version from a real device. Anything else makes the whole
    /// request <c>sync.malformed</c>, because there would be no change ID to report a result under.
    /// </summary>
    public static bool IsAnswerable(SyncChangeDto? change) =>
        change is { ChangeId: var id, Version: { DeviceId: var device } } && id != Guid.Empty && device != Guid.Empty;

    public static IncomingChange ToChange(SyncChangeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var isDelete = dto.Operation == SyncOperations.Delete;
        var payload = dto.Operation == SyncOperations.Upsert ? Payload(dto) : null;
        return new IncomingChange(
            dto.ChangeId, dto.EntityType ?? "", dto.EntityId, isDelete,
            new HlcStamp(dto.Version.WallClockMs, dto.Version.Counter, new DeviceId(dto.Version.DeviceId)), payload);
    }

    public static ChangeResultDto ToDto(ChangeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var result = verdict.Result.Outcome switch
        {
            ChangeOutcome.Applied => ChangeResults.Applied,
            ChangeOutcome.Superseded => ChangeResults.Superseded,
            _ => ChangeResults.Rejected,
        };
        return new ChangeResultDto(verdict.ChangeId, result, verdict.Result.Reason);
    }

    public static PullResponse ToDto(PullPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PullResponse([.. page.Records.Select(ToDto)], page.Cursor, page.HasMore, page.BootstrapHead);
    }

    private static SyncRecordDto ToDto(PulledRecord record)
    {
        var version = new HlcStampDto(record.Version.WallClockMs, record.Version.Counter, record.Version.DeviceId.Value);
        var dto = new SyncRecordDto(record.EntityType, record.EntityId, version, record.IsDeleted, record.Seq);
        return record.Payload switch
        {
            CareEntryPayload c => dto with
            {
                CareEntry = new CareEntryDto(c.Kind, c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy, c.Source,
                    c.SourcePromptVersion, c.Note, Element(c.DetailsJson)),
            },
            BabyProfilePayload b => dto with { BabyProfile = new BabyProfileDto(b.Name, b.Nicknames, b.BirthDate, b.DueDate) },
            HouseholdSettingsPayload s => dto with { HouseholdSettings = new HouseholdSettingsDto(s.VolumeUnit, s.TimeZoneId, s.HiddenKinds) },
            DismissalPayload d => dto with { DuplicateDismissal = new DuplicateDismissalDto(d.EntryA, d.EntryB) },
            _ => throw new ArgumentOutOfRangeException(nameof(record), record.Payload.GetType().Name, "Unknown payload."),
        };
    }

    private static ChangePayload? Payload(SyncChangeDto dto) => dto.EntityType switch
    {
        SyncEntityTypes.CareEntry when dto.CareEntry is { } c => new CareEntryPayload(
            c.Kind ?? "", c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy ?? "", c.Source ?? "",
            c.SourcePromptVersion, c.Note, c.Details.ValueKind == JsonValueKind.Undefined ? "" : c.Details.GetRawText()),
        SyncEntityTypes.BabyProfile when dto.BabyProfile is { } b => new BabyProfilePayload(b.Name ?? "", b.Nicknames ?? [], b.BirthDate, b.DueDate),
        SyncEntityTypes.HouseholdSettings when dto.HouseholdSettings is { } s => new HouseholdSettingsPayload(s.VolumeUnit ?? "", s.TimeZoneId ?? "", s.HiddenKinds ?? []),
        SyncEntityTypes.DuplicateDismissal when dto.DuplicateDismissal is { } d => new DismissalPayload(d.EntryA, d.EntryB),
        _ => null,
    };

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
