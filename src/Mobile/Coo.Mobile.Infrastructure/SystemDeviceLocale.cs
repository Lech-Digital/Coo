using System.Globalization;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Infrastructure;

/// <summary>The phone's region and time zone, as defaults for a new household (ADR 0006 §5).</summary>
internal sealed class SystemDeviceLocale(TimeProvider time) : IDeviceLocale
{
    public string TimeZoneId => time.LocalTimeZone.Id;

    public bool PrefersOunces => RegionInfo.CurrentRegion.TwoLetterISORegionName == "US";
}
