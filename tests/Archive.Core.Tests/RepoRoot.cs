namespace Archive.Core.Tests;

/// <summary>
/// Locates the repository root by walking up from the test assembly's output directory
/// until a solution file appears.
/// </summary>
/// <remarks>
/// Tests that read real repository content — the Telegram golden fixtures in
/// <c>tests/fixtures/telegram</c>, the csproj files checked by
/// <see cref="SolutionLayoutTests"/> — cannot rely on the working directory, which differs
/// between `dotnet test`, an IDE runner and CI. Walking up to the .slnx is stable in all three.
/// </remarks>
internal static class RepoRoot
{
    internal static DirectoryInfo Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
            {
                return dir;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"No .slnx or .sln found walking up from '{AppContext.BaseDirectory}'.");
    }
}
