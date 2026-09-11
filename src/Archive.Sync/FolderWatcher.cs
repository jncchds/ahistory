using Archive.Import;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Sync;

/// <summary>What looking at a watched folder found.</summary>
public enum FolderCheckOutcome
{
    /// <summary>Exactly as it was when last imported. Nothing was read.</summary>
    Unchanged,

    /// <summary>Changed, and imported.</summary>
    Imported,

    /// <summary>The folder is not there — an unplugged drive, a sync client that has not run.</summary>
    Missing,

    /// <summary>Changed, and the import stopped. Tried again at the next change or the next poll.</summary>
    Failed,
}

public sealed record FolderCheck(
    WatchedFolder Folder,
    FolderCheckOutcome Outcome,
    ImportStats? Stats = null,
    string? Error = null);

/// <summary>
/// Re-imports watched folders when what is in them changes.
/// </summary>
/// <remarks>
/// <para>
/// The free half of "live" (docs/live-sources-plan.md, route A). SMS Backup &amp; Restore can write
/// a backup every night, Google Takeout can export every two months, DiscordChatExporter can run on
/// a schedule — and every reader here is already idempotent (P3), so a folder that is simply read
/// again whenever it changes keeps the archive as current as the exports are.
/// </para>
/// <para>
/// A change is acted on only after the folder has been quiet for a while. A sync client writing a
/// backup produces a burst of events, and importing at the first one reads a half-written file —
/// which a strict reader refuses (D20), so it is not dangerous, only wasted. A timer looks again
/// every so often as well, because file-system events are not delivered for every kind of volume
/// (network shares, some sync clients), and a watcher that silently stops watching is worse than
/// one that is merely late.
/// </para>
/// <para>
/// Folders are checked one at a time. SQLite has one writer, and two imports at once only take
/// turns at batch boundaries while both pay the cost of waiting.
/// </para>
/// <para>
/// Like the AI runner, it lives and dies with the app. Nothing watches while it is closed; the
/// first check after launch catches up on whatever arrived meanwhile.
/// </para>
/// </remarks>
public sealed class FolderWatcher : IDisposable
{
    private readonly ImportRunner _runner;
    private readonly SyncSettingsStore _store;
    private readonly string _savePath;
    private readonly ILogger _log;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _poll;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private Timer? _debounce;
    private Timer? _poller;
    private bool _started;

    /// <param name="quiet">How long a folder must stop changing before it is read. Default 30 seconds.</param>
    /// <param name="poll">How often every folder is looked at regardless. Default 15 minutes.</param>
    public FolderWatcher(
        ImportRunner runner,
        SyncSettingsStore store,
        string savePath,
        ILogger<FolderWatcher>? logger = null,
        TimeSpan? quiet = null,
        TimeSpan? poll = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);

        _savePath = savePath;
        _log = logger ?? NullLogger<FolderWatcher>.Instance;
        _quiet = quiet ?? TimeSpan.FromSeconds(30);
        _poll = poll ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>Raised after each folder is looked at, on a background thread.</summary>
    public event Action<FolderCheck>? Checked;

    public IReadOnlyList<WatchedFolder> Folders => _store.Load().FoldersFor(_savePath);

    public void Add(WatchedFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        _store.Update(settings => settings.Watch(_savePath, folder));

        _log.LogInformation("Watching {FolderPath} for new exports.", Path.GetFullPath(folder.Path));

        Rewatch();
    }

    public void Remove(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        _store.Update(settings => settings.Unwatch(_savePath, folderPath));

        Rewatch();
    }

    /// <summary>Starts watching, and schedules a first look at every folder.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _debounce = new Timer(_ => RunChecks(), null, Timeout.Infinite, Timeout.Infinite);
            _poller = new Timer(_ => RunChecks(), null, TimeSpan.Zero, _poll);
        }

        Rewatch();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _started = false;
            _debounce?.Dispose();
            _poller?.Dispose();
            _debounce = null;
            _poller = null;

            DisposeWatchers();
        }
    }

    /// <summary>Looks at every watched folder now, importing the ones that changed.</summary>
    public async Task<IReadOnlyList<FolderCheck>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var results = new List<FolderCheck>();

            foreach (var folder in Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await Task.Run(() => Check(folder), cancellationToken).ConfigureAwait(false);
                results.Add(result);

                Checked?.Invoke(result);
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Looks at one folder and imports it if it changed since it was last imported.</summary>
    /// <remarks>
    /// Never throws. A folder that fails to import is reported and tried again later; one bad backup
    /// must not stop the others being read, or the watcher from running.
    /// </remarks>
    public FolderCheck Check(WatchedFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        try
        {
            if (!Directory.Exists(folder.Path))
            {
                return new FolderCheck(folder, FolderCheckOutcome.Missing);
            }

            if (_runner.IsAlreadyImported(folder.Path, folder.Platform))
            {
                return new FolderCheck(folder, FolderCheckOutcome.Unchanged);
            }

            var stats = _runner.Run(
                folder.Path,
                sourceId: folder.SourceId,
                ownerAccountId: folder.OwnerAccountId,
                platform: folder.Platform);

            return new FolderCheck(folder, FolderCheckOutcome.Imported, stats);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The runner has already logged the import's own failure with its id; this line only
            // records that a watched folder was the one that caused it.
            _log.LogWarning(ex, "Watched folder {FolderPath} could not be imported.", folder.Path);

            return new FolderCheck(folder, FolderCheckOutcome.Failed, Error: ex.Message);
        }
    }

    private void RunChecks()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A timer callback that throws takes the process with it.
                _log.LogError(ex, "Checking watched folders failed.");
            }
        });
    }

    /// <summary>One file-system watcher per folder, rebuilt whenever the list changes.</summary>
    private void Rewatch()
    {
        lock (_lock)
        {
            DisposeWatchers();

            if (!_started)
            {
                return;
            }

            foreach (var folder in Folders.Where(f => Directory.Exists(f.Path)))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                       | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };

                    watcher.Changed += OnChange;
                    watcher.Created += OnChange;
                    watcher.Deleted += OnChange;
                    watcher.Renamed += OnChange;
                    watcher.EnableRaisingEvents = true;

                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    // The poll still covers it; say why events will not.
                    _log.LogWarning(ex, "Cannot receive change events for {FolderPath}; polling only.", folder.Path);
                }
            }
        }
    }

    /// <summary>Restarts the quiet period. The check runs once changes stop arriving.</summary>
    private void OnChange(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            _debounce?.Change(_quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}
