namespace Coo.Mobile;

/// <summary>
/// Built into every APK (Spec 010 §6). Neither is a secret: a client ID identifies the app to Google
/// (docs/google-sign-in-setup.md §6), and the API is public.
/// </summary>
internal static class BuildConstants
{
    /// <summary>The Web client of Google Cloud project coo-app-510411: the audience the server checks.</summary>
    public const string GoogleWebClientId = "282211746968-l54aogbkec1gf1g0rp0n7bk2mhbg2c7r.apps.googleusercontent.com";

    public static readonly Uri ApiBaseAddress = new("https://api.coo.lechdigital.nl/");
}
