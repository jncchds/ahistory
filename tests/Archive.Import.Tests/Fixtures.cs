namespace Archive.Import.Tests;

/// <summary>
/// Locates the golden Telegram export fixtures in <c>tests/fixtures/telegram</c>.
/// </summary>
/// <remarks>
/// Found by walking up from the test assembly's output directory to the solution file, because
/// the working directory differs between `dotnet test`, an IDE runner and CI. The fixtures are
/// deliberately real files on disk rather than embedded strings: the importer's job is to read an
/// export folder, and a test that hands it a string would not exercise that at all.
/// </remarks>
internal static class Fixtures
{
    /// <summary>The tests/fixtures folder.</summary>
    internal static string Root => Path.Combine(RepoRoot(), "tests", "fixtures");

    /// <summary>An empty throwaway folder, for fixtures a test builds itself.</summary>
    internal static string Temp(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ahistory-tests", name, Guid.NewGuid().ToString("N"));

        // Qualified: this class has its own Directory method, which would otherwise win.
        System.IO.Directory.CreateDirectory(folder);

        return folder;
    }

    internal static string Directory(string name) =>
        Path.Combine(RepoRoot(), "tests", "fixtures", "telegram", name);

    internal static string ResultJson(string name) =>
        Path.Combine(Directory(name), "result.json");

    internal static Stream OpenResultJson(string name) =>
        File.OpenRead(ResultJson(name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"No .slnx or .sln found walking up from '{AppContext.BaseDirectory}'.");
    }
}
