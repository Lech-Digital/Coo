namespace Coo.Domain.Households;

/// <summary>A baby's age on a given day, actual and corrected (Spec 001 §5.2, ADR 0008 §2).</summary>
public sealed record BabyAge(int ActualDays, int CorrectedDays, bool IsCorrected, bool IsBeforeDueDate)
{
    /// <summary>Corrected age is used until the actual age reaches this many months.</summary>
    public const int CorrectedAgeUntilMonths = 24;

    public int ActualWeeks => ActualDays / 7;

    public int CorrectedWeeks => CorrectedDays / 7;

    public static BabyAge On(BabyProfile profile, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var actual = Math.Max(0, today.DayNumber - profile.BirthDate.DayNumber);
        var preterm = profile.DueDate is { } due && due > profile.BirthDate;
        var underLimit = today < profile.BirthDate.AddMonths(CorrectedAgeUntilMonths);

        if (!preterm || !underLimit)
            return new BabyAge(actual, actual, IsCorrected: false, IsBeforeDueDate: false);

        var dueDate = profile.DueDate!.Value;
        var corrected = Math.Max(0, today.DayNumber - dueDate.DayNumber);
        return new BabyAge(actual, corrected, IsCorrected: true, IsBeforeDueDate: today < dueDate);
    }

    /// <summary>Whole calendar months between two dates, for display ("5 months").</summary>
    public static int WholeMonths(DateOnly from, DateOnly to)
    {
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        return to.Day < from.Day ? months - 1 : months;
    }
}
