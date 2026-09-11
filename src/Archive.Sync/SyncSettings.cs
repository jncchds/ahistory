namespace Archive.Sync;

/// <summary>
/// What keeps a save current, per machine: the folders watched for new exports, and the
/// connected accounts.
/// </summary>
/// <remarks>
/// <para>
/// Beside <c>ai.json</c> rather than inside the save, for the reasons that file gives and one of
/// its own. A watched folder is a path on this machine, and P7 says the save must not carry one —
/// the same save opened on a laptop has none of these folders. And an account's credentials must
/// never travel with a save someone copies or hands over.
/// </para>
/// <para>
/// Which chats of an account belong in the archive is the opposite: a decision about the archive,
/// so it lives in the save (<c>sync_chat</c>) and moves with it.
/// </para>
/// </remarks>
public sealed class SyncSettings
{
    /// <summary>Watched folders, keyed by the full path of the save they import into.</summary>
    public Dictionary<string, List<WatchedFolder>> WatchedFolders { get; set; } = new(StringComparer.Ordinal);

    public TelegramSettings Telegram { get; set; } = new();

    public IReadOnlyList<WatchedFolder> FoldersFor(string savePath) =>
        WatchedFolders.TryGetValue(Key(savePath), out var folders) ? folders : [];

    /// <summary>Adds a folder to a save's list, or replaces the entry already there for that path.</summary>
    public void Watch(string savePath, WatchedFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var key = Key(savePath);

        if (!WatchedFolders.TryGetValue(key, out var folders))
        {
            folders = [];
            WatchedFolders[key] = folders;
        }

        var normalized = folder with { Path = System.IO.Path.GetFullPath(folder.Path) };

        folders.RemoveAll(f => SamePath(f.Path, normalized.Path));
        folders.Add(normalized);
    }

    public bool Unwatch(string savePath, string folderPath)
    {
        if (!WatchedFolders.TryGetValue(Key(savePath), out var folders))
        {
            return false;
        }

        var full = System.IO.Path.GetFullPath(folderPath);

        return folders.RemoveAll(f => SamePath(f.Path, full)) > 0;
    }

    private static string Key(string savePath) => System.IO.Path.GetFullPath(savePath);

    /// <summary>
    /// Paths compare the way the file system does: case-insensitively on Windows and macOS'
    /// default volumes, exactly elsewhere. Getting it wrong on Windows lists one folder twice.
    /// </summary>
    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

/// <summary>A folder a scheduled export lands in, and how to read it.</summary>
/// <param name="Path">The folder, as a full path.</param>
/// <param name="Platform">Which format to read, when the folder holds more than one. Null takes the best match.</param>
/// <param name="SourceId">
/// The source it imports into — the answer the import page already asked for the first time, kept
/// so a scheduled re-import never has to ask again.
/// </param>
/// <param name="OwnerAccountId">Which account is the user's, for the formats that do not say (D25).</param>
public sealed record WatchedFolder(
    string Path,
    string? Platform = null,
    string? SourceId = null,
    string? OwnerAccountId = null);

/// <summary>The Telegram connection, as this machine has it configured.</summary>
/// <remarks>
/// Off until switched on. The session itself — which is the account, not a setting — is kept in a
/// protected file of its own (<see cref="SecretFile"/>), never here.
/// </remarks>
public sealed class TelegramSettings
{
    /// <summary>Whether the app may connect to Telegram at all. Nothing is contacted while false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The application id from my.telegram.org. Every Telegram client needs one; this app uses
    /// the user's own, so nothing about their account runs under anyone else's.
    /// </summary>
    public int? ApiId { get; set; }

    public string? ApiHash { get; set; }

    /// <summary>Whether to keep listening for new messages while the app is open.</summary>
    public bool Live { get; set; }

    public bool DownloadPhotos { get; set; } = true;

    public bool DownloadVoice { get; set; } = true;

    public bool DownloadVideo { get; set; }

    public bool DownloadFiles { get; set; }

    /// <summary>Anything larger is recorded as not downloaded, whatever its kind.</summary>
    public long MaxDownloadBytes { get; set; } = 20L * 1024 * 1024;

    public bool HasApplication => ApiId is > 0 && !string.IsNullOrWhiteSpace(ApiHash);
}
