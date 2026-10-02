using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Platform.Auth;

/// <summary>The model, as Settings → About phone shows it: "Pixel 10". Shown in the account's device list.</summary>
internal sealed class AndroidDeviceName : IDeviceName
{
    public string Name => string.IsNullOrWhiteSpace(Android.OS.Build.Model) ? "Android" : Android.OS.Build.Model;
}
