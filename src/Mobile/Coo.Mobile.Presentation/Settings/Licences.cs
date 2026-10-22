namespace Coo.Mobile.Presentation.Settings;

/// <summary>One shipped component and its licence text, bundled as <c>Resources/Raw/licences/{AssetFile}</c>.</summary>
public sealed record Licence(string Name, string LicenceName, string AssetFile, IReadOnlyList<string> PackageIds);

/// <summary>
/// What the app ships (Spec 009 §3.6). A test fails when a mobile project references a package that is not listed
/// here, so the page cannot fall behind.
/// </summary>
public static class Licences
{
    public static IReadOnlyList<Licence> All { get; } =
    [
        new("Figtree", "SIL Open Font License 1.1", "figtree-ofl.txt", []),
        new(".NET and .NET MAUI", "MIT", "dotnet-mit.txt", ["Microsoft.Maui.Controls", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Data.Sqlite.Core", "Plugin.InAppBilling"]),
        new("CommunityToolkit.Mvvm", "MIT", "communitytoolkit-mit.txt", ["CommunityToolkit.Mvvm"]),
        new("SQLitePCLRaw", "Apache License 2.0", "sqlitepclraw-apache2.txt", ["SQLitePCLRaw.bundle_e_sqlite3"]),
        new("SQLite", "Public domain", "sqlite-public-domain.txt", []),
        new("AndroidX Credential Manager", "Apache License 2.0 (bindings: MIT)", "androidx-credentials-apache2.txt",
            ["Xamarin.AndroidX.Credentials", "Xamarin.AndroidX.Credentials.PlayServicesAuth"]),
        new("Google Identity Services (Sign in with Google)", "Android Software Development Kit License (bindings: MIT)", "google-identity-notice.txt",
            ["Xamarin.GoogleAndroid.Libraries.Identity.GoogleId"]),
        new("Google Play In-App Updates and Review", "Play Core SDK Terms of Service (bindings: MIT)", "google-play-core-notice.txt",
            ["Xamarin.Google.Android.Play.App.Update", "Xamarin.Google.Android.Play.Review"]),
        new("AndroidX Fragment", "Apache License 2.0 (bindings: MIT)", "androidx-credentials-apache2.txt", ["Xamarin.AndroidX.Fragment.Ktx"]),
    ];
}
