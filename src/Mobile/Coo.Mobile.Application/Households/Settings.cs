using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Households;

public static class SettingsErrors
{
    /// <summary>At least one kind stays visible (Spec 009 §3.2).</summary>
    public const string AllKindsHidden = "settings.all-kinds-hidden";
}

/// <summary>What the baby page saves (Spec 009 §3.1).</summary>
public sealed record BabyProfileChange(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate);

/// <summary>The baby page's Save: every field in one write, with its outbox row.</summary>
public sealed class UpdateBabyProfile(IHouseholdStore households, IVersionClock clock, TimeProvider time)
{
    public async Task<Result> ExecuteAsync(BabyProfile baby, BabyProfileChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(change);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var result = baby.Revise(change.Name, change.Nicknames, change.BirthDate, change.DueDate, await clock.NextAsync(cancellationToken), today);
        if (result.IsSuccess)
            await households.SaveBabyAsync(baby, cancellationToken);
        return result;
    }
}

/// <summary>ml or oz for the household (Spec 009 §3.3). Display only: stored amounts stay in ml.</summary>
public sealed class ChangeVolumeUnit(IHouseholdStore households, IVersionClock clock)
{
    public async Task<Result> ExecuteAsync(HouseholdSettings settings, VolumeUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.VolumeUnit == unit)
            return Result.Success;

        var result = settings.SetVolumeUnit(unit, await clock.NextAsync(cancellationToken));
        if (result.IsSuccess)
            await households.SaveSettingsAsync(settings, cancellationToken);
        return result;
    }
}

/// <summary>
/// Shows or hides one kind (Spec 009 §3.2, ADR 0010 §6). Codes this build does not know are kept, and hiding the last
/// visible known kind is refused; showing one is always allowed.
/// </summary>
public sealed class SetHiddenKinds(IHouseholdStore households, IVersionClock clock)
{
    /// <summary>
    /// The kinds with a tile on Today, which Settings can hide. Health and growth are logged from their own sheet and
    /// never hidden, so they do not count towards "at least one stays visible".
    /// </summary>
    public static IReadOnlyList<string> DailyKinds { get; } = ["feeding", "diaper", "sleep", "pump"];

    public async Task<Result> ExecuteAsync(HouseholdSettings settings, EntryKindCode kind, bool hidden, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var next = new HashSet<EntryKindCode>(settings.HiddenKinds);
        if (!(hidden ? next.Add(kind) : next.Remove(kind)))
            return Result.Success;
        if (hidden && DailyKinds.All(code => next.Contains(EntryKindCode.Known(code))))
            return Result.Failure(SettingsErrors.AllKindsHidden);

        var result = settings.SetHiddenKinds(next, await clock.NextAsync(cancellationToken));
        if (result.IsSuccess)
            await households.SaveSettingsAsync(settings, cancellationToken);
        return result;
    }
}
