using Coo.Domain.CareLog;
using Coo.Domain.Households;

namespace Coo.Mobile.Presentation.Today;

/// <summary>
/// Which kinds the household shows (Spec 009 §3.2, ADR 0010 §6). Hiding removes ways to log and totals, never
/// entries or running timers.
/// </summary>
public static class KindVisibility
{
    public static bool IsShown(HouseholdSettings household, string kindCode)
    {
        ArgumentNullException.ThrowIfNull(household);
        return EntryKindCode.From(kindCode) is not { IsSuccess: true } code || !household.HiddenKinds.Contains(code.Value);
    }
}
