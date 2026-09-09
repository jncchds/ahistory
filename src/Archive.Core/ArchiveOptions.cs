namespace Archive.Core;

/// <summary>
/// Locates the save: the SQLite database file and the media folder that belongs to it.
/// </summary>
/// <remarks>
/// A save is self-contained — a <c>.db</c> file plus a media folder beside it — so that copying
/// two paths moves an entire archive between machines. Nothing in the database stores an
/// absolute path to media; files are addressed by content hash and resolved against
/// <see cref="ResolveMediaDirectory"/> at read time. That is what lets a save written on Windows
/// open unchanged on Linux.
/// </remarks>
public sealed class ArchiveOptions
{
    public const string SectionName = "Archive";

    /// <summary>Path to the SQLite database file. Required.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// Media root. When null, defaults to a folder beside the database named after it —
    /// <c>archive.db</c> gets <c>archive.media</c>.
    /// </summary>
    public string? MediaDirectory { get; set; }

    public string ResolveMediaDirectory()
    {
        if (!string.IsNullOrWhiteSpace(MediaDirectory))
        {
            return Path.GetFullPath(MediaDirectory);
        }

        var full = Path.GetFullPath(DatabasePath);
        var directory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException($"'{DatabasePath}' has no containing directory.");

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(full) + ".media");
    }

    /// <summary>
    /// Throws if the configuration cannot produce a working save.
    /// </summary>
    /// <remarks>
    /// Called from PostConfigure so that bad configuration fails at startup with a sentence
    /// naming the setting, rather than surfacing later as an empty window or a confusing
    /// SQLite error several layers down.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(DatabasePath)} is required (env: AHISTORY_{SectionName}__{nameof(DatabasePath)}).");
        }

        // Path.GetFullPath throws on genuinely malformed paths; surface that as a config error.
        try
        {
            _ = Path.GetFullPath(DatabasePath);
            _ = ResolveMediaDirectory();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(DatabasePath)} is not a usable path: '{DatabasePath}'.", ex);
        }
    }
}
