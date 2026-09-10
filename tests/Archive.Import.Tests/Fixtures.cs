namespace Archive.Import.Tests;

/// <summary>
/// Throwaway folders for the exports a test builds.
/// </summary>
/// <remarks>
/// Outside the repository, deliberately. An export is what someone's private correspondence looks
/// like on disk, and a folder of them inside a source tree is one careless copy away from being a
/// real one — so the shapes live in code (<see cref="Exports"/>) and the files exist only while a
/// test runs. <c>NoArchiveDataInTheRepositoryTests</c> is what keeps that true.
/// </remarks>
internal static class Fixtures
{
    /// <summary>An empty throwaway folder, named after the test that asked for it.</summary>
    internal static string Temp(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ahistory-tests", name, Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(folder);

        return folder;
    }
}
