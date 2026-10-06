using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Households;

/// <summary>A new provisional family's settings from the device's defaults (ADR 0003 §1, ADR 0006 §5).</summary>
public sealed class ProvisionalSettings(IVersionClock clock, IDeviceLocale locale, TimeProvider time)
{
    public async Task<HouseholdSettings> CreateAsync(CancellationToken cancellationToken)
    {
        var id = new HouseholdId(Guid.CreateVersion7(time.GetUtcNow()));
        var unit = locale.PrefersOunces ? VolumeUnit.Ounces : VolumeUnit.Millilitres;
        var settings = HouseholdSettings.Create(id, unit, locale.TimeZoneId, await clock.NextAsync(cancellationToken));
        return settings.IsSuccess
            ? settings.Value
            : throw new InvalidOperationException($"The device time zone '{locale.TimeZoneId}' is unusable: {settings.Error}.");
    }
}
