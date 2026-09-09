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

    /// <summary>
    /// The desktop head stays thin: the UI, and the logging setup it has to choose.
    /// </summary>
    /// <remarks>
    /// A head that reaches straight into Archive.Data or Archive.Import has started doing work
    /// that belongs in a view model, where it cannot be tested without a window. Logging is the
    /// one exception by nature — picking sinks is a hosting decision, and it is what keeps the
    /// libraries on abstractions only.
    /// </remarks>
    [Fact]
    public void The_desktop_head_depends_only_on_the_ui_and_logging_projects()
    {
        var references = ProjectReferencesOf("src/Archive.Desktop/Archive.Desktop.csproj");

        Assert.Equal(["Archive.Logging", "Archive.Ui"], references);
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

    /// <summary>
    /// AGENTS.md P1: the archive is a chat history app first, and must build and run with no AI
    /// component present at all.
    /// </summary>
    /// <remarks>
    /// This is enforced by a test rather than by intention because the erosion is always
    /// reasonable-looking: one embedding call added to a query path, one model client injected
    /// into a view model, and suddenly opening your own history depends on a runtime being
    /// installed and a background job having finished. AI belongs in its own projects, which
    /// the spine must never reference.
    /// </remarks>
    [Fact]
    public void No_core_project_takes_an_ai_dependency()
    {
        string[] spine =
        [
            "Archive.Core", "Archive.Data", "Archive.Import", "Archive.Media",
            "Archive.Logging", "Archive.Ui", "Archive.Desktop", "Archive.Cli",
        ];

        // Substrings, matched case-insensitively against package ids.
        string[] forbidden =
        [
            "whisper", "onnx", "llama", "openai", "anthropic", "semantickernel",
            "semantic.kernel", "tensorflow", "torch", "transformers", "ollama",
            "sqlite-vec", "sqlitevec", "embedding", "ml.net", "microsoft.ml",
        ];

        foreach (var project in SourceProjects().Where(p => spine.Contains(ProjectName(p))))
        {
            var packages = XDocument.Load(project)
                .Descendants("PackageReference")
                .Select(r => r.Attribute("Include")?.Value ?? string.Empty);

            foreach (var package in packages)
            {
                var hit = forbidden.FirstOrDefault(f =>
                    package.Contains(f, StringComparison.OrdinalIgnoreCase));

                Assert.True(
                    hit is null,
                    $"{ProjectName(project)} references '{package}'. AGENTS.md P1: the archive must "
                    + "build and run with no AI component. Put this in a separate project.");
            }
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
