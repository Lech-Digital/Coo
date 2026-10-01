using System.Xml.Linq;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Presentation.Tests;

public sealed class LicencesTests
{
    [Fact]
    public void All_EveryMobilePackageReference_HasALicenceEntry()
    {
        // Arrange
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Coo.slnx")))
            root = root.Parent;
        var packages = Directory.GetFiles(Path.Combine(root!.FullName, "src", "Mobile"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Distinct();

        // Act
        var missing = packages.Except(Licences.All.SelectMany(l => l.PackageIds)).ToList();

        // Assert
        missing.Should().BeEmpty("every shipped package is listed on the licences page (Spec 009 §3.6)");
    }

    [Fact]
    public void All_EveryEntry_HasItsBundledText()
    {
        // Arrange
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Coo.slnx")))
            root = root.Parent;
        var folder = Path.Combine(root!.FullName, "src", "Mobile", "Coo.Mobile", "Resources", "Raw", "licences");

        // Act
        var missing = Licences.All.Where(l => !File.Exists(Path.Combine(folder, l.AssetFile))).Select(l => l.AssetFile).ToList();

        // Assert
        missing.Should().BeEmpty();
    }

    [Fact]
    public void Project_LicenceTexts_AreBundledAsAppAssets()
    {
        // Arrange: found on the Pixel 10 — the texts existed in the repo but not in the APK, and a tap crashed.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Coo.slnx")))
            root = root.Parent;
        var project = XDocument.Load(Path.Combine(root!.FullName, "src", "Mobile", "Coo.Mobile", "Coo.Mobile.csproj"));

        // Act
        var assets = project.Descendants("MauiAsset").Select(e => (string?)e.Attribute("Include")).OfType<string>().ToList();

        // Assert
        assets.Should().Contain(a => a.Replace('/', '\\').StartsWith("Resources\\Raw\\", StringComparison.Ordinal),
            "FileSystem.OpenAppPackageFileAsync only finds files packaged as MauiAsset");
    }
}
