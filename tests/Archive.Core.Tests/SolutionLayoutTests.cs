using System.Xml.Linq;

namespace Archive.Core.Tests;

/// <summary>
/// Guards the structural rules the whole solution rests on.
/// </summary>
/// <remarks>
/// The dependency rule (Core depends on nothing; everything depends on Core; the heads depend
/// on everything) is the kind of constraint that is stated in a README and then quietly broken
/// by a convenient using-directive six months later. A test makes it fail at `dotnet test`
/// instead of during a later extraction.
/// </remarks>
public sealed class SolutionLayoutTests
{
    /// <summary>Properties owned by Directory.Build.props. A csproj repeating one is drift waiting to happen.</summary>
    private static readonly string[] SharedProperties =
    [
        "TargetFramework", "LangVersion", "Nullable", "ImplicitUsings",
        "TreatWarningsAsErrors", "InvariantGlobalization",
    ];

    [Fact]
    public void Core_depends_on_nothing()
    {
        var references = ProjectReferencesOf("src/Archive.Core/Archive.Core.csproj");

        Assert.Empty(references);
    }

    [Fact]
    public void The_desktop_head_depends_only_on_the_ui_project()
    {
        var references = ProjectReferencesOf("src/Archive.Desktop/Archive.Desktop.csproj");

        Assert.Equal(["Archive.Ui"], references);
    }

    [Fact]
    public void Every_source_project_depends_on_core_except_core_itself()
    {
        foreach (var project in SourceProjects().Where(p => ProjectName(p) != "Archive.Core"))
        {
            var references = ProjectReferencesOf(project);
            var reachesCore = references.Contains("Archive.Core")
                || references.Contains("Archive.Ui");   // the head reaches Core transitively

            Assert.True(reachesCore, $"{ProjectName(project)} does not reference Archive.Core.");
        }
    }

    [Fact]
    public void No_project_repeats_a_shared_build_property()
    {
        foreach (var project in AllProjects())
        {
            var declared = XDocument.Load(project)
                .Descendants("PropertyGroup")
                .Elements()
                .Select(e => e.Name.LocalName)
                .Intersect(SharedProperties)
                .ToArray();

            Assert.True(
                declared.Length == 0,
                $"{ProjectName(project)} repeats {string.Join(", ", declared)} — these belong to Directory.Build.props only.");
        }
    }

    private static IEnumerable<string> SourceProjects() =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot.Find().FullName, "src"), "*.csproj", SearchOption.AllDirectories);

    private static IEnumerable<string> AllProjects() =>
        Directory.EnumerateFiles(RepoRoot.Find().FullName, "*.csproj", SearchOption.AllDirectories);

    private static string ProjectName(string path) => Path.GetFileNameWithoutExtension(path);

    private static string[] ProjectReferencesOf(string relativeOrAbsolutePath)
    {
        var path = Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(RepoRoot.Find().FullName, relativeOrAbsolutePath);

        return [.. XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(NormalizeSeparators(r.Attribute("Include")!.Value)))
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Turns a csproj Include path into forward-slash form regardless of which separator it
    /// was written with, so this test reads the same on Windows and Linux. The backslash is
    /// written as a code point because it must survive tooling that rewrites escape sequences.
    /// </summary>
    private static string NormalizeSeparators(string path) => path.Replace((char)92, '/');
}
