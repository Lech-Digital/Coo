using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Platform;

/// <summary>Android's own 12- or 24-hour setting (ADR 0006 §5): times follow the phone, not the language.</summary>
internal sealed class AndroidClockFormat : IClockFormat
{
    public bool Is24Hour => Android.Text.Format.DateFormat.Is24HourFormat(Android.App.Application.Context);
}
