using System.Xml.Linq;

namespace Coo.Architecture.Tests;

/// <summary>Reads the repository's project files. The csproj XML is the source of truth for the graph.</summary>
internal static class Repository
{
    public static DirectoryInfo Root { get; } = FindRoot();

    public static IReadOnlyList<ProjectFile> SourceProjects { get; } =
        Directory.GetFiles(Path.Combine(Root.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(ProjectFile.Load)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<ProjectFile> AllProjects { get; } =
        Directory.GetFiles(Root.FullName, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(ProjectFile.Load)
            .ToList();

    private static DirectoryInfo FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Coo.slnx")))
            dir = dir.Parent;
        return dir ?? throw new InvalidOperationException("Coo.slnx not found above the test output directory.");
    }
}

internal sealed record ProjectFile(string Name, string Path, string RelativePath, XDocument Xml)
{
    public static ProjectFile Load(string path)
    {
        var relative = System.IO.Path.GetRelativePath(Repository.Root.FullName, path).Replace('\\', '/');
        return new(System.IO.Path.GetFileNameWithoutExtension(path), path, relative, XDocument.Load(path));
    }

    public IReadOnlyList<string> ProjectReferences =>
        Xml.Descendants("ProjectReference")
            .Select(e => System.IO.Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<string> PackageReferences =>
        Xml.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToList();

    public IReadOnlyList<string> TargetFrameworks =>
        Xml.Descendants("TargetFramework").Concat(Xml.Descendants("TargetFrameworks"))
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

    public bool IsMultiTargeted => Xml.Descendants("TargetFrameworks").Any();

    public bool IsBackend => RelativePath.StartsWith("src/Backend/", StringComparison.Ordinal);

    public bool IsMobile => RelativePath.StartsWith("src/Mobile/", StringComparison.Ordinal);
}
