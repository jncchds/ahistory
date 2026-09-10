namespace Archive.Import.Synthetic;

/// <summary>
/// Attachment files for a generated export.
/// </summary>
/// <remarks>
/// The bytes are meaningless on purpose. The media store is content-addressed and never decodes
/// anything, so what matters about an attachment is that two references to one file hash the same
/// and two different files do not — which random bytes from a fixed seed give exactly, without
/// putting a real image in the repository.
/// </remarks>
public static class SyntheticMedia
{
    /// <summary>
    /// Writes a file of <paramref name="byteCount"/> deterministic bytes at a relative path.
    /// </summary>
    /// <param name="folder">The export folder the path is relative to.</param>
    /// <param name="relativePath">The path as the export references it, with forward slashes.</param>
    /// <param name="seed">
    /// Fixed, so the same call always produces the same bytes and therefore the same content hash.
    /// Two files that must differ need two seeds.
    /// </param>
    public static string Write(string folder, string relativePath, int seed = 1, int byteCount = 2048)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var path = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bytes = new byte[byteCount];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        return relativePath;
    }
}
