using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog;

/// <summary>
/// Everything the shared code needs to know about one kind of entry (Spec 001 §7.1, ADR 0010 §4).
/// Shared code never switches on the kind: it asks the descriptor.
/// </summary>
public interface IEntryKind
{
    EntryKindCode Code { get; }

    SubjectType Subject { get; }

    Type DetailsType { get; }

    /// <summary>The kind's plausibility threshold for an open session; null for point-only kinds.</summary>
    TimeSpan? RunningLongAfter { get; }

    DuplicateRule? Duplicates { get; }

    TimingRule TimingFor(EntryDetails details);

    EntryTiming DeriveTiming(EntryDetails details);

    Result Validate(EntryDetails details, EntryTiming timing);

    /// <summary>Rules about changing one details value into another, such as an immutable variant.</summary>
    Result ValidateChange(EntryDetails before, EntryDetails after);

    IEnumerable<Warning> Plausibility(EntryDetails details, EntryTiming timing, PlausibilityContext context);
}

/// <summary>The typed base for a kind's descriptor. Each kind lives in its own folder under <c>Kinds/</c>.</summary>
public abstract class EntryKind<TDetails> : IEntryKind
    where TDetails : EntryDetails
{
    public abstract EntryKindCode Code { get; }

    public abstract SubjectType Subject { get; }

    public Type DetailsType => typeof(TDetails);

    public virtual TimeSpan? RunningLongAfter => null;

    public virtual DuplicateRule? Duplicates => null;

    public TimingRule TimingFor(EntryDetails details) => TimingFor(Cast(details));

    public EntryTiming DeriveTiming(EntryDetails details) => DeriveTiming(Cast(details));

    public Result Validate(EntryDetails details, EntryTiming timing) => Validate(Cast(details), timing);

    public Result ValidateChange(EntryDetails before, EntryDetails after) => ValidateChange(Cast(before), Cast(after));

    public IEnumerable<Warning> Plausibility(EntryDetails details, EntryTiming timing, PlausibilityContext context) =>
        Plausibility(Cast(details), timing, context);

    protected abstract TimingRule TimingFor(TDetails details);

    protected abstract Result Validate(TDetails details, EntryTiming timing);

    protected virtual EntryTiming DeriveTiming(TDetails details) =>
        throw new InvalidOperationException($"Kind '{Code}' does not derive timing from its details.");

    protected virtual Result ValidateChange(TDetails before, TDetails after) => Result.Success;

    protected virtual IEnumerable<Warning> Plausibility(TDetails details, EntryTiming timing, PlausibilityContext context) => [];

    private TDetails Cast(EntryDetails details) =>
        details as TDetails ?? throw new ArgumentException(
            $"Kind '{Code}' expects {typeof(TDetails).Name}, not {details?.GetType().Name ?? "null"}.", nameof(details));
}
