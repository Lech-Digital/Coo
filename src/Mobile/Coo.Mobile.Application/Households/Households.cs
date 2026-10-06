using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Households;

/// <summary>Households, their settings and their babies on this phone (Spec 004 §2).</summary>
public interface IHouseholdStore
{
    /// <summary>
    /// The active household (<c>app_state.active_household</c>, set by sync) first, then by ID: callers that show one
    /// household take the first live one (Spec 011 ruling 7).
    /// </summary>
    Task<IReadOnlyList<HouseholdSettings>> ListHouseholdsAsync(CancellationToken cancellationToken);

    /// <summary>Saves the settings and appends their outbox row, in one transaction.</summary>
    Task SaveSettingsAsync(HouseholdSettings settings, CancellationToken cancellationToken);

    Task<IReadOnlyList<BabyProfile>> ListBabiesAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Saves the baby and appends its outbox row, in one transaction.</summary>
    Task SaveBabyAsync(BabyProfile baby, CancellationToken cancellationToken);
}

/// <summary>What the phone says about where it is: the defaults for a new household (ADR 0006 §5).</summary>
public interface IDeviceLocale
{
    /// <summary>The IANA time zone ID.</summary>
    string TimeZoneId { get; }

    /// <summary>The device region defaults to ounces (the US), rather than millilitres.</summary>
    bool PrefersOunces { get; }
}

/// <summary>
/// On first launch, creates the provisional household and its settings (ADR 0003 §1); afterwards, returns it.
/// No account is needed for any of this.
/// </summary>
public sealed class EnsureProvisionalHousehold(IHouseholdStore households, IVersionClock clock, IDeviceLocale locale, TimeProvider time)
{
    public async Task<HouseholdSettings> ExecuteAsync(CancellationToken cancellationToken)
    {
        var existing = await households.ListHouseholdsAsync(cancellationToken);
        if (existing.FirstOrDefault(h => !h.IsDeleted) is { } household)
            return household;

        var settings = await new ProvisionalSettings(clock, locale, time).CreateAsync(cancellationToken);
        await households.SaveSettingsAsync(settings, cancellationToken);
        return settings;
    }
}
