using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Households;

/// <summary>First launch: the household's first baby, born or expected (Spec 006 §3, §3a).</summary>
public sealed class SetUpBaby(IHouseholdStore households, IVersionClock clock, TimeProvider time)
{
    public async Task<Result<BabyProfile>> ExecuteAsync(
        HouseholdId household, string name, DateOnly? birthDate, DateOnly? dueDate, BabySex? sex, CancellationToken cancellationToken)
    {
        var now = time.GetLocalNow();
        var created = BabyProfile.Create(
            new BabyId(Guid.CreateVersion7(time.GetUtcNow())), household, name, [], birthDate, dueDate,
            await clock.NextAsync(cancellationToken), DateOnly.FromDateTime(now.DateTime), sex);

        if (created.IsSuccess)
            await households.SaveBabyAsync(created.Value, cancellationToken);
        return created;
    }
}

/// <summary>The baby has arrived (Spec 006 §3a): an expected baby becomes a born one.</summary>
public sealed class RecordBirth(IHouseholdStore households, IVersionClock clock, TimeProvider time)
{
    public async Task<Result> ExecuteAsync(BabyProfile baby, DateOnly birthDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var result = baby.RecordBirth(birthDate, await clock.NextAsync(cancellationToken), today);
        if (result.IsSuccess)
            await households.SaveBabyAsync(baby, cancellationToken);
        return result;
    }
}
