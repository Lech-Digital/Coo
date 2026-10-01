using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.CareLog;

/// <summary>
/// Every care-log entry, whatever its kind (Spec 001 §6, ADR 0010 §1). The core fields are shared by all
/// kinds; <see cref="Details"/> holds the kind's own data. All rules run through one pipeline (§6.4).
/// </summary>
public sealed class CareEntry : SyncedRecord<EntryId>
{
    private CareEntry(
        EntryId id,
        HouseholdId householdId,
        IEntryKind kind,
        EntrySubject subject,
        EntryTiming timing,
        EntryDetails details,
        PersonRef loggedBy,
        EntrySource source,
        Note? note,
        HlcStamp version,
        bool isDeleted)
        : base(id, householdId, version, isDeleted)
    {
        Kind = kind;
        Subject = subject;
        Timing = timing;
        Details = details;
        LoggedBy = loggedBy;
        Source = source;
        Note = note;
    }

    public IEntryKind Kind { get; }

    public EntryKindCode KindCode => Kind.Code;

    public EntrySubject Subject { get; }

    public EntryTiming Timing { get; private set; }

    public EntryDetails Details { get; private set; }

    public PersonRef LoggedBy { get; }

    public EntrySource Source { get; }

    public Note? Note { get; private set; }

    /// <summary>
    /// Creates an entry. The kind comes from the details type. Pass <paramref name="timing"/> only for kinds
    /// that do not derive it from their details.
    /// </summary>
    public static Result<CareEntry> Create(
        EntryId id,
        HouseholdId householdId,
        EntrySubject subject,
        EntryDetails details,
        EntryTiming? timing,
        PersonRef loggedBy,
        EntrySource source,
        HlcStamp version,
        DateTimeOffset now,
        Note? note = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(loggedBy);
        ArgumentNullException.ThrowIfNull(source);

        if (EntryKinds.For(details.GetType()) is not { } kind)
            return Result<CareEntry>.Failure(EntryErrors.KindUnknown);

        if (subject.Type != kind.Subject)
            return Result<CareEntry>.Failure(EntryErrors.SubjectNotAllowed);

        var resolved = ResolveTiming(kind, details, timing);
        if (resolved.IsFailure)
            return Result<CareEntry>.Failure(resolved.Error!);

        var rules = CheckRules(kind, details, resolved.Value, now);
        if (rules.IsFailure)
            return Result<CareEntry>.Failure(rules.Error!);

        return new CareEntry(id, householdId, kind, subject, resolved.Value, details, loggedBy, source, note, version, isDeleted: false);
    }

    /// <summary>
    /// Applies a kind-specific change to the details, for example <c>d =&gt; d.SwitchSide(at)</c>, and re-runs
    /// the pipeline. The timing is re-derived for kinds that derive it.
    /// </summary>
    public Result Revise<TDetails>(Func<TDetails, Result<TDetails>> change, HlcStamp version, DateTimeOffset now)
        where TDetails : EntryDetails
    {
        ArgumentNullException.ThrowIfNull(change);

        var writable = EnsureWritable(version);
        if (writable.IsFailure)
            return writable;

        if (Details is not TDetails current)
            return Result.Failure(EntryErrors.DetailsTypeMismatch);

        var changed = change(current);
        if (changed.IsFailure)
            return changed;

        var next = changed.Value;
        if (EntryKinds.For(next.GetType()) != Kind)
            return Result.Failure(EntryErrors.DetailsTypeMismatch);

        // The change rule runs before the shape check: a breast feed revised into a bottle must report the
        // method change, not the timing shape that would follow from it.
        var allowed = Kind.ValidateChange(Details, next);
        if (allowed.IsFailure)
            return allowed;

        var timing = Kind.TimingFor(next) == TimingRule.DerivedFromDetails ? Kind.DeriveTiming(next) : Timing;
        var outcome = Result.FirstFailure(
            () => CheckShape(Kind, next, timing),
            () => CheckRules(Kind, next, timing, now));
        if (outcome.IsFailure)
            return outcome;

        Details = next;
        Timing = timing;
        Stamp(version);
        return Result.Success;
    }

    /// <summary>Changes the timing of a kind that does not derive it, such as ending a sleep.</summary>
    public Result Retime(EntryTiming timing, HlcStamp version, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(timing);

        var outcome = Result.FirstFailure(
            () => EnsureWritable(version),
            () => Result.Check(Kind.TimingFor(Details) != TimingRule.DerivedFromDetails, EntryErrors.TimingDerived),
            () => CheckShape(Kind, Details, timing),
            () => CheckRules(Kind, Details, timing, now));
        if (outcome.IsFailure)
            return outcome;

        Timing = timing;
        Stamp(version);
        return Result.Success;
    }

    public Result SetNote(Note? note, HlcStamp version)
    {
        var writable = EnsureWritable(version);
        if (writable.IsFailure)
            return writable;

        Note = note;
        Stamp(version);
        return Result.Success;
    }

    /// <summary>Rebuilds stored or pulled state without re-running the creation rules (Spec 001 §3.5).</summary>
    internal static CareEntry Rehydrate(
        EntryId id,
        HouseholdId householdId,
        IEntryKind kind,
        EntrySubject subject,
        EntryTiming timing,
        EntryDetails details,
        PersonRef loggedBy,
        EntrySource source,
        Note? note,
        HlcStamp version,
        bool isDeleted) =>
        new(id, householdId, kind, subject, timing, details, loggedBy, source, note, version, isDeleted);

    private static Result<EntryTiming> ResolveTiming(IEntryKind kind, EntryDetails details, EntryTiming? timing)
    {
        if (kind.TimingFor(details) == TimingRule.DerivedFromDetails)
            return timing is null ? Result<EntryTiming>.Ok(kind.DeriveTiming(details)) : Result<EntryTiming>.Failure(EntryErrors.TimingDerived);

        if (timing is null)
            return Result<EntryTiming>.Failure(EntryErrors.TimingShapeNotAllowed);

        var shape = CheckShape(kind, details, timing);
        return shape.IsSuccess ? Result<EntryTiming>.Ok(timing) : Result<EntryTiming>.Failure(shape.Error!);
    }

    private static Result CheckShape(IEntryKind kind, EntryDetails details, EntryTiming timing) =>
        kind.TimingFor(details) switch
        {
            TimingRule.PointOnly => Result.Check(timing is PointTiming, EntryErrors.TimingShapeNotAllowed),
            TimingRule.SessionOnly => Result.Check(timing is SessionTiming, EntryErrors.TimingShapeNotAllowed),
            _ => Result.Check(timing == kind.DeriveTiming(details), EntryErrors.TimingShapeNotAllowed),
        };

    private static Result CheckRules(IEntryKind kind, EntryDetails details, EntryTiming timing, DateTimeOffset now) =>
        Result.FirstFailure(
            () => Result.Check(timing.Instants.All(i => i - now <= EntryErrors.MaxAheadOfNow), EntryErrors.TooFarInFuture),
            () => kind.Validate(details, timing));
}
