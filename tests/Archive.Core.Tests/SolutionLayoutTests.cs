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
    /// Every project that ships to a user who never enables AI, and therefore must not carry a
    /// model runtime.
    /// </summary>
    /// <remarks>
    /// Not the UI and not the heads: those may reference <c>Archive.Ai</c> freely, because AI
    /// there is a page and a switch (decisions.md D31). What this list protects is the storage
    /// layer, where a package reference is not a feature but a native, per-RID binary that ships
    /// and loads for everyone.
    /// </remarks>
    private static readonly string[] Storage =
    [
        "Archive.Core", "Archive.Data", "Archive.Import", "Archive.Media", "Archive.Logging",
    ];

    /// <summary>
    /// AGENTS.md P1: the archive is a chat history app first, and must run with no AI component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule this enforces is narrower than it once was. It is not that the UI must be unable
    /// to see the AI project — that turned out to buy nothing, and made a settings page awkward
    /// to reach. It is that <c>sqlite-vec</c> and <c>Whisper.net</c> are native binaries in a
    /// build already over 100 MB per platform, and a package referenced from the storage layer
    /// ships and loads whether or not anyone switched anything on.
    /// </para>
    /// <para>
    /// The behavioural half of P1 — no model, no process, no network, no AI in the window when it
    /// is off — is checked where it lives, in the view-model tests.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_storage_project_takes_an_ai_dependency()
    {
        var spine = Storage;

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
                    $"{ProjectName(project)} references '{package}'. AGENTS.md P1: a user who never "
                    + "enables AI must not be shipped a model runtime. Put this in Archive.Ai.");
            }
        }
    }

    /// <summary>
    /// The storage layer does not reference the AI project either.
    /// </summary>
    /// <remarks>
    /// The package rule above would not catch it: <c>Archive.Ai</c> carries no forbidden package
    /// today, because talking to an OpenAI-shaped endpoint needs nothing but HttpClient. It will
    /// at A6, and by then a reference from <c>Archive.Data</c> would be load-bearing and awkward
    /// to remove — which is why the direction is fixed now, while it costs nothing.
    /// </remarks>
    [Fact]
    public void No_storage_project_references_the_ai_project()
    {
        foreach (var project in SourceProjects().Where(p => Storage.Contains(ProjectName(p))))
        {
            Assert.DoesNotContain("Archive.Ai", ProjectReferencesOf(project));
        }
    }

    /// <summary>
    /// The storage layer does not reference the sync project either.
    /// </summary>
    /// <remarks>
    /// Same direction, same reason as AI. <c>Archive.Sync</c> talks to platforms over the network,
    /// and the storage layer is what every user gets whether or not they ever connect an account:
    /// a reference from <c>Archive.Import</c> would put a Telegram client in the import path, where
    /// reading a folder must stay a thing that touches nothing but that folder.
    /// </remarks>
    [Fact]
    public void No_storage_project_references_the_sync_project()
    {
        foreach (var project in SourceProjects().Where(p => Storage.Contains(ProjectName(p))))
        {
            Assert.DoesNotContain("Archive.Sync", ProjectReferencesOf(project));
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
