using Coo.Domain.Common;

namespace Coo.Mobile.Application.Households;

/// <summary>
/// Twins and siblings: which baby this phone shows (Today, Log, Stats, the Advisor, the quick log). Choosing one tells
/// every open screen through <see cref="Changed"/>; each reloads, and the store lists that baby first from then on.
/// </summary>
public sealed class BabyFocus(IHouseholdStore households)
{
    public event Action? Changed;

    public async Task FocusAsync(BabyId baby, CancellationToken cancellationToken)
    {
        await households.FocusBabyAsync(baby, cancellationToken);
        Changed?.Invoke();
    }
}
