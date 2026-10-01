using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.Households;

/// <summary>
/// A baby in a household (Spec 001 §5.1), born or still expected (Spec 006 §3a). An expected baby has a due date and
/// no birth date yet; <see cref="RecordBirth"/> turns it into a born one.
/// </summary>
public sealed class BabyProfile : SyncedRecord<BabyId>
{
    public const int NameMaxLength = 40;
    public const int MaxNicknames = 5;
    public static readonly int DueDateEarliestDaysBeforeBirth = 6 * 7;
    public static readonly int DueDateLatestDaysAfterBirth = 24 * 7;

    /// <summary>While expected, a due date may lie up to 6 weeks in the past: babies come late.</summary>
    public static readonly int ExpectedDueDateLatestDaysPast = 6 * 7;

    /// <summary>While expected, a due date may lie up to 44 weeks ahead: a whole pregnancy, and a margin.</summary>
    public static readonly int ExpectedDueDateLatestDaysAhead = 44 * 7;

    private BabyProfile(
        BabyId id, HouseholdId householdId, string name, IReadOnlyList<string> nicknames,
        DateOnly? birthDate, DateOnly? dueDate, HlcStamp version, bool isDeleted)
        : base(id, householdId, version, isDeleted)
    {
        Name = name;
        Nicknames = nicknames;
        BirthDate = birthDate;
        DueDate = dueDate;
    }

    public string Name { get; private set; }

    /// <summary>What parents actually call the baby; hidden from the AI like the name (ADR 0005 §4a).</summary>
    public IReadOnlyList<string> Nicknames { get; private set; }

    /// <summary>Null while the baby is expected (Spec 006 §3a).</summary>
    public DateOnly? BirthDate { get; private set; }

    /// <summary>
    /// For an expected baby, when the baby is due. For a born preterm baby, it gives the corrected age
    /// (ADR 0008 §2).
    /// </summary>
    public DateOnly? DueDate { get; private set; }

    public bool IsBorn => BirthDate is not null;

    public static Result<BabyProfile> Create(
        BabyId id, HouseholdId householdId, string name, IEnumerable<string> nicknames,
        DateOnly? birthDate, DateOnly? dueDate, HlcStamp version, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(nicknames);

        var cleanName = CleanName(name);
        var cleanNicknames = nicknames.Select(CleanName).ToList();
        var outcome = Result.FirstFailure(
            () => CheckName(cleanName),
            () => CheckNicknames(cleanName, cleanNicknames),
            () => CheckDates(birthDate, dueDate, today));

        return outcome.IsSuccess
            ? new BabyProfile(id, householdId, cleanName!, cleanNicknames!.AsReadOnly()!, birthDate, dueDate, version, isDeleted: false)
            : Result<BabyProfile>.Failure(outcome.Error!);
    }

    public Result Rename(string name, HlcStamp version)
    {
        var clean = CleanName(name);
        return Apply(version, () => Result.FirstFailure(() => CheckName(clean), () => CheckNicknames(clean, Nicknames)), () => Name = clean!);
    }

    public Result SetNicknames(IEnumerable<string> nicknames, HlcStamp version)
    {
        ArgumentNullException.ThrowIfNull(nicknames);
        var clean = nicknames.Select(CleanName).ToList();
        return Apply(version, () => CheckNicknames(Name, clean), () => Nicknames = clean!.AsReadOnly()!);
    }

    /// <summary>The baby has arrived. The due date is kept, for corrected age.</summary>
    public Result RecordBirth(DateOnly birthDate, HlcStamp version, DateOnly today) =>
        Apply(
            version,
            () => Result.FirstFailure(() => Result.Check(!IsBorn, BabyErrors.AlreadyBorn), () => CheckDates(birthDate, DueDate, today)),
            () => BirthDate = birthDate);

    /// <summary>Corrects the birth date of a born baby.</summary>
    public Result SetBirthDate(DateOnly birthDate, HlcStamp version, DateOnly today) =>
        Apply(
            version,
            () => Result.FirstFailure(() => Result.Check(IsBorn, BabyErrors.NotBornYet), () => CheckDates(birthDate, DueDate, today)),
            () => BirthDate = birthDate);

    public Result SetDueDate(DateOnly? dueDate, HlcStamp version, DateOnly today) =>
        Apply(version, () => CheckDates(BirthDate, dueDate, today), () => DueDate = dueDate);

    internal static BabyProfile Rehydrate(
        BabyId id, HouseholdId householdId, string name, IReadOnlyList<string> nicknames,
        DateOnly? birthDate, DateOnly? dueDate, HlcStamp version, bool isDeleted) =>
        new(id, householdId, name, nicknames, birthDate, dueDate, version, isDeleted);

    private Result Apply(HlcStamp version, Func<Result> rules, Action change)
    {
        var outcome = Result.FirstFailure(() => EnsureWritable(version), rules);
        if (outcome.IsFailure)
            return outcome;

        change();
        Stamp(version);
        return Result.Success;
    }

    private static string? CleanName(string? name) => name?.Trim();

    private static Result CheckName(string? name) =>
        Result.Check(name is { Length: > 0 and <= NameMaxLength }, BabyErrors.NameInvalid);

    private static Result CheckNicknames(string? name, IReadOnlyCollection<string?> nicknames)
    {
        if (nicknames.Count > MaxNicknames || nicknames.Any(n => n is not { Length: > 0 and <= NameMaxLength }))
            return Result.Failure(BabyErrors.NameInvalid);

        var keys = nicknames.Select(n => NameKey.From(n!)).ToList();
        var nameKey = name is null ? null : NameKey.From(name);
        return Result.Check(keys.Distinct().Count() == keys.Count && (nameKey is null || !keys.Contains(nameKey)), BabyErrors.NicknameDuplicate);
    }

    private static Result CheckDates(DateOnly? birthDate, DateOnly? dueDate, DateOnly today) => (birthDate, dueDate) switch
    {
        (null, null) => Result.Failure(BabyErrors.BirthOrDueDateRequired),
        ({ } birth, _) when birth > today.AddDays(1) => Result.Failure(BabyErrors.BirthInFuture),
        ({ } birth, { } due) => Result.Check(
            due >= birth.AddDays(-DueDateEarliestDaysBeforeBirth) && due <= birth.AddDays(DueDateLatestDaysAfterBirth),
            BabyErrors.DueDateOutOfRange),
        (null, { } due) => Result.Check(
            due >= today.AddDays(-ExpectedDueDateLatestDaysPast) && due <= today.AddDays(ExpectedDueDateLatestDaysAhead),
            BabyErrors.DueDateOutOfRange),
        _ => Result.Success,
    };
}

public static class BabyErrors
{
    public const string NameInvalid = "baby.name-invalid";
    public const string NicknameDuplicate = "baby.nickname-duplicate";
    public const string BirthInFuture = "baby.birth-in-future";
    public const string DueDateOutOfRange = "baby.due-date-out-of-range";
    public const string BirthOrDueDateRequired = "baby.birth-or-due-date-required";
    public const string AlreadyBorn = "baby.already-born";
    public const string NotBornYet = "baby.not-born-yet";
}
