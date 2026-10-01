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
        new(".NET and .NET MAUI", "MIT", "dotnet-mit.txt", ["Microsoft.Maui.Controls", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Data.Sqlite.Core"]),
        new("CommunityToolkit.Mvvm", "MIT", "communitytoolkit-mit.txt", ["CommunityToolkit.Mvvm"]),
        new("SQLitePCLRaw", "Apache License 2.0", "sqlitepclraw-apache2.txt", ["SQLitePCLRaw.bundle_e_sqlite3"]),
        new("SQLite", "Public domain", "sqlite-public-domain.txt", []),
    ];
}
