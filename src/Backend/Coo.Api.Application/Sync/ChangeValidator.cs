using Coo.Codecs;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

/// <summary>
/// One change against the rules of its schema version (Spec 011 §6.4, ADR 0002 §11): the codecs, the single-record
/// Domain rules, the role rule, the stamp bound. A failure's code is the <c>rejected</c> reason. Rules that span records
/// (overlapping sleeps, a baby that is not in the household) are never checked here (ADR 0002 §9).
/// </summary>
public static class ChangeValidator
{
    public static Result<SyncWrite> Validate(IncomingChange change, HouseholdId household, Membership member, int schemaVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(member);

        if (schemaVersion > SchemaVersions.Current)
            return Reject(RejectionReasons.SchemaUnsupported);
        if (change.Version.IsTooFarAhead(now.ToUnixTimeMilliseconds()))
            return Reject(RejectionReasons.StampInFuture);
        if (change.EntityType is null || !SyncEntityTypes.All.Contains(change.EntityType) || change.EntityId == Guid.Empty)
            return Reject(RejectionReasons.ChangeMalformed);
        // A Viewer follows the log and changes nothing in it.
        if (!member.CanLog)
            return Reject(RejectionReasons.RoleForbidden);
        if ((change.EntityType is SyncEntityTypes.BabyProfile or SyncEntityTypes.HouseholdSettings) && !member.CanManageBaby)
            return Reject(RejectionReasons.RoleForbidden);
        if (change.EntityType == SyncEntityTypes.HouseholdSettings && change.EntityId != household.Value)
            return Reject(RejectionReasons.HouseholdMismatch);
        if (change.IsDelete)
            return new TombstoneWrite(change.EntityType, change.EntityId, change.Version);

        return change.Payload switch
        {
            CareEntryPayload p when change.EntityType == SyncEntityTypes.CareEntry => ForCareEntry(change, household, p, now),
            BabyProfilePayload p when change.EntityType == SyncEntityTypes.BabyProfile => ForBaby(change, household, p, now),
            HouseholdSettingsPayload p when change.EntityType == SyncEntityTypes.HouseholdSettings => ForSettings(change, household, p),
            DismissalPayload p when change.EntityType == SyncEntityTypes.DuplicateDismissal => ForDismissal(change, household, p),
            _ => Reject(RejectionReasons.ChangeMalformed),
        };
    }

    private static Result<SyncWrite> ForCareEntry(IncomingChange change, HouseholdId household, CareEntryPayload p, DateTimeOffset now)
    {
        if (EntryKindCode.From(p.Kind) is not { IsSuccess: true } code || EntryKinds.Find(code.Value) is not { } kind
            || EntryDetailsCodecs.For(code.Value) is not { } codec)
            return Reject(EntryErrors.KindUnknown);

        EntryDetails details;
        switch (codec.Decode(p.DetailsJson ?? ""))
        {
            case Decoded decoded:
                details = decoded.Details;
                break;
            case Unrepresentable:
                // Newer than this server: the server is meant to be the newest, so the phone is ahead of it.
                return Reject(RejectionReasons.SchemaUnsupported);
            case Malformed malformed:
                return Reject(malformed.Reason);
            default:
                return Reject(RejectionReasons.ChangeMalformed);
        }

        if (p.LoggedBy == SyncText.ThisDevice || p.SubjectPerson == SyncText.ThisDevice)
            return Reject(RejectionReasons.PersonUnresolved);

        var loggedBy = SyncText.ParsePerson(p.LoggedBy);
        var source = SyncText.ParseSource(p.Source, p.SourcePromptVersion);
        EntrySubject? subject = (p.SubjectBabyId, p.SubjectPerson) switch
        {
            ({ } baby, null) when baby != Guid.Empty => EntrySubject.Baby(new BabyId(baby)),
            (null, { } person) when SyncText.ParsePerson(person) is { } who => EntrySubject.Caregiver(who),
            _ => null,
        };
        if (loggedBy is null || source is null || subject is null)
            return Reject(RejectionReasons.ChangeMalformed);

        var note = Note.From(p.Note);
        if (note.IsFailure)
            return Reject(note.Error!.Code);

        EntryTiming timing;
        if (p.IsSession)
        {
            var range = TimeRange.Create(p.StartAt, p.EndAt);
            if (range.IsFailure)
                return Reject(range.Error!.Code);
            timing = EntryTiming.Session(range.Value);
        }
        else if (p.EndAt is null)
        {
            timing = EntryTiming.Point(p.StartAt);
        }
        else
        {
            return Reject(EntryErrors.TimingShapeNotAllowed);
        }

        var derived = kind.TimingFor(details) == TimingRule.DerivedFromDetails;
        var created = CareEntry.Create(
            new EntryId(change.EntityId), household, subject, details, derived ? null : timing, loggedBy, source, change.Version, now, note.Value);
        if (created.IsFailure)
            return Reject(created.Error!.Code);

        // A kind that derives its timing (a breast feed) must carry the timing its details give.
        if (derived && created.Value.Timing != timing)
            return Reject(EntryErrors.TimingShapeNotAllowed);

        return new CareEntryWrite(created.Value, p.DetailsJson!);
    }

    private static Result<SyncWrite> ForBaby(IncomingChange change, HouseholdId household, BabyProfilePayload p, DateTimeOffset now)
    {
        var created = BabyProfile.Create(
            new BabyId(change.EntityId), household, p.Name, p.Nicknames ?? [], p.BirthDate, p.DueDate, change.Version,
            DateOnly.FromDateTime(now.UtcDateTime),
            p.Sex switch { "girl" => BabySex.Girl, "boy" => BabySex.Boy, _ => null });
        return created.IsSuccess ? new BabyProfileWrite(created.Value) : Reject(created.Error!.Code);
    }

    private static Result<SyncWrite> ForSettings(IncomingChange change, HouseholdId household, HouseholdSettingsPayload p)
    {
        VolumeUnit? unit = p.VolumeUnit switch
        {
            "ml" => VolumeUnit.Millilitres,
            "oz" => VolumeUnit.Ounces,
            _ => null,
        };
        var hidden = (p.HiddenKinds ?? []).Select(EntryKindCode.From).ToList();
        if (unit is null || hidden.Any(h => h.IsFailure))
            return Reject(RejectionReasons.ChangeMalformed);

        var created = HouseholdSettings.Create(household, unit.Value, p.TimeZoneId ?? "", change.Version, hidden.Select(h => h.Value));
        return created.IsSuccess ? new HouseholdSettingsWrite(created.Value) : Reject(created.Error!.Code);
    }

    private static Result<SyncWrite> ForDismissal(IncomingChange change, HouseholdId household, DismissalPayload p)
    {
        if (p.EntryA == Guid.Empty || p.EntryB == Guid.Empty || p.EntryA == p.EntryB)
            return Reject(RejectionReasons.ChangeMalformed);

        var pair = new EntryPair(new EntryId(p.EntryA), new EntryId(p.EntryB));
        return new DismissalWrite(DuplicateDismissal.Create(new DismissalId(change.EntityId), household, pair, change.Version));
    }

    private static Result<SyncWrite> Reject(string reason) => Result<SyncWrite>.Failure(reason);
}
