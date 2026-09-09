namespace Archive.Media;

/// <summary>
/// Turns a content hash into the path it lives at, and back.
/// </summary>
/// <remarks>
/// Sharded two levels by the first four hex characters — <c>media/ab/cd/abcd...</c> — because a
/// real archive holds tens of thousands of files, and directories with that many entries are
/// slow to enumerate on every platform and unpleasant to open in a file manager on some. Two
/// levels of 256 gives a comfortable spread without deep nesting.
///
/// Paths are always built with forward slashes and combined with <see cref="Path.Combine"/>, so
/// a save written on Windows resolves unchanged on Linux and macOS.
/// </remarks>
public static class MediaAddress
{
    /// <summary>Length of a lowercase hex SHA-256 digest.</summary>
    public const int HashLength = 64;

    public static string RelativePath(string hash, string? extension = null)
    {
        Validate(hash);

        return Path.Combine(hash[..2], hash[2..4], hash + NormalizeExtension(extension));
    }

    public static string FullPath(string mediaRoot, string hash, string? extension = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);

        return Path.Combine(mediaRoot, RelativePath(hash, extension));
    }

    /// <summary>
    /// Lowercases an extension and guarantees a leading dot, or returns empty for none.
    /// </summary>
    /// <remarks>
    /// Telegram exports are inconsistent about case (<c>.JPG</c> and <c>.jpg</c> both appear),
    /// and on a case-sensitive filesystem that would produce two paths for one hash.
    /// </remarks>
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim().ToLowerInvariant();

        return trimmed[0] == '.' ? trimmed : "." + trimmed;
    }

    private static void Validate(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (hash.Length != HashLength)
        {
            throw new ArgumentException(
                $"Expected a {HashLength}-character hex digest, got {hash.Length} characters.", nameof(hash));
        }

        foreach (var c in hash)
        {
            var isLowerHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';

            if (!isLowerHex)
            {
                throw new ArgumentException($"'{hash}' is not a lowercase hex digest.", nameof(hash));
            }
        }
    }
}
