using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Infrastructure.Sync;

/// <summary>
/// <c>/api/sync</c>, <c>/api/households/{id}/membership</c> and <c>/api/consents</c>, through the refreshing handler.
/// Contracts are mapped here: details become a JSON object on the wire and a string again on the way in, exactly as
/// received (ADR 0010 §3). A record of a type this build does not know is skipped; the cursor still moves past it.
/// </summary>
internal sealed class SyncClient(HttpClient http) : ISyncApi, IConsentApi
{
    private static readonly Uri PushUri = new("api/sync/push", UriKind.Relative);
    private static readonly Uri ConsentsUri = new("api/consents", UriKind.Relative);

    public async Task<IReadOnlyList<PushResult>> PushAsync(
        HouseholdId household, DeviceId device, IReadOnlyList<OutgoingChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        PushRequest body;
        try
        {
            body = new PushRequest(household.Value, device.Value, SchemaVersions.Current, [.. changes.Select(ToDto)]);
        }
        catch (JsonException e)
        {
            // Details this phone stored that are not JSON: nothing can be sent now, and nothing was decided.
            throw new SyncUnavailableException("A local change could not be put on the wire.", e);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, PushUri) { Content = JsonContent.Create(body, SyncJsonContext.Default.PushRequest) };
        using var response = await SyncHttp.SendAsync(http, request, household, cancellationToken);
        var answer = await SyncHttp.ReadAsync(response, SyncJsonContext.Default.PushResponse, cancellationToken);
        return [.. (answer.Results ?? []).Select(FromDto).OfType<PushResult>()];
    }

    public async Task<PulledPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, CancellationToken cancellationToken)
    {
        var head = bootstrapHead is { } value ? string.Create(CultureInfo.InvariantCulture, $"&bootstrapHead={value}") : "";
        var uri = new Uri(
            string.Create(CultureInfo.InvariantCulture, $"api/sync/pull?household={household.Value:D}&cursor={cursor}&schemaVersion={SchemaVersions.Current}{head}"),
            UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SyncHttp.SendAsync(http, request, household, cancellationToken);
        var page = await SyncHttp.ReadAsync(response, SyncJsonContext.Default.PullResponse, cancellationToken);
        return new PulledPage([.. (page.Records ?? []).Select(FromDto).OfType<PulledRecord>()], page.Cursor, page.HasMore, page.BootstrapHead);
    }

    public async Task<MembershipStatus> GetMembershipAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"api/households/{household.Value:D}/membership", UriKind.Relative));
            using var response = await SyncHttp.SendAsync(http, request, household, cancellationToken);
            var answer = await SyncHttp.ReadAsync(response, SyncJsonContext.Default.MembershipStatusResponse, cancellationToken);
            return answer.Status switch
            {
                MembershipStatuses.Revoked => MembershipStatus.Revoked,
                MembershipStatuses.Active => MembershipStatus.Active,
                _ => MembershipStatus.Unknown,
            };
        }
        catch (Exception e) when (e is SyncUnavailableException or SyncRefusedException)
        {
            // Only a 200 that says "revoked" may lead to a deletion; anything else keeps the soft-lock (ADR 0002 §10).
            return MembershipStatus.Unknown;
        }
    }

    public async Task RecordAsync(string appVersion, string language, CancellationToken cancellationToken)
    {
        var body = new ConsentRequest(CareDataConsent.Purpose, CareDataConsent.CurrentWording, appVersion, language);
        using var request = new HttpRequestMessage(HttpMethod.Post, ConsentsUri) { Content = JsonContent.Create(body, SyncJsonContext.Default.ConsentRequest) };
        using var response = await SyncHttp.SendAsync(http, request, default, cancellationToken);
    }

    private static SyncChangeDto ToDto(OutgoingChange change)
    {
        var version = new HlcStampDto(change.Version.WallClockMs, change.Version.Counter, change.Version.DeviceId.Value);
        var dto = new SyncChangeDto(change.ChangeId, change.EntityType, change.EntityId, change.IsDelete ? SyncOperations.Delete : SyncOperations.Upsert, version);
        return change.Payload switch
        {
            CareEntryPayload c => dto with
            {
                CareEntry = new CareEntryDto(c.Kind, c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy, c.Source,
                    c.SourcePromptVersion, c.Note, Element(c.DetailsJson)),
            },
            BabyProfilePayload b => dto with { BabyProfile = new BabyProfileDto(b.Name, b.Nicknames, b.BirthDate, b.DueDate) },
            HouseholdSettingsPayload s => dto with { HouseholdSettings = new HouseholdSettingsDto(s.VolumeUnit, s.TimeZoneId, s.HiddenKinds) },
            DismissalPayload d => dto with { DuplicateDismissal = new DuplicateDismissalDto(d.EntryA, d.EntryB) },
            _ => dto,
        };
    }

    private static PushResult? FromDto(ChangeResultDto result) => result.Result switch
    {
        ChangeResults.Applied => new PushResult(result.ChangeId, PushOutcome.Applied, null),
        ChangeResults.Superseded => new PushResult(result.ChangeId, PushOutcome.Superseded, null),
        ChangeResults.Rejected => new PushResult(result.ChangeId, PushOutcome.Rejected, result.Reason),
        _ => null,
    };

    private static PulledRecord? FromDto(SyncRecordDto record)
    {
        if (record.Version is null || record.Version.DeviceId == Guid.Empty)
            return null;
        var version = new HlcStamp(record.Version.WallClockMs, record.Version.Counter, new DeviceId(record.Version.DeviceId));
        RecordPayload? payload = record.EntityType switch
        {
            SyncEntityTypes.CareEntry when record.CareEntry is { Details.ValueKind: not JsonValueKind.Undefined } c => new CareEntryPayload(
                c.Kind, c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy, c.Source, c.SourcePromptVersion, c.Note,
                c.Details.GetRawText()),
            SyncEntityTypes.BabyProfile when record.BabyProfile is { } b => new BabyProfilePayload(b.Name, b.Nicknames ?? [], b.BirthDate, b.DueDate),
            SyncEntityTypes.HouseholdSettings when record.HouseholdSettings is { } s => new HouseholdSettingsPayload(s.VolumeUnit, s.TimeZoneId, s.HiddenKinds ?? []),
            SyncEntityTypes.DuplicateDismissal when record.DuplicateDismissal is { } d => new DismissalPayload(d.EntryA, d.EntryB),
            _ => null,
        };
        return payload is null ? null : new PulledRecord(record.EntityType, record.EntityId, version, record.IsDeleted, payload);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
