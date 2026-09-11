using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Sync;

/// <summary>
/// Reads and writes <see cref="SyncSettings"/> as <c>sync.json</c> beside the other per-machine
/// configuration.
/// </summary>
/// <remarks>
/// Written whole through a temporary file and a replace, like <c>ai.json</c>: a file truncated by a
/// crash mid-write is a first run nobody can diagnose. Updates go through one lock, because the
/// watcher and the window both change this file and a read-modify-write that interleaves loses one
/// of the two changes.
/// </remarks>
public sealed class SyncSettingsStore
{
    private const string FileName = "sync.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly ILogger _log;

    public SyncSettingsStore(string? directory = null, ILogger<SyncSettingsStore>? logger = null)
    {
        Directory = directory ?? DefaultDirectory;
        Path = System.IO.Path.Combine(Directory, FileName);
        _log = logger ?? NullLogger<SyncSettingsStore>.Instance;
    }

    /// <summary>Beside the logs and <c>ai.json</c>.</summary>
    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ahistory");

    public string Directory { get; }

    public string Path { get; }

    /// <summary>Raised after every save, so the pages showing these settings can follow.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The settings as they stand, or defaults.
    /// </summary>
    /// <remarks>
    /// A corrupt file gives defaults rather than an exception — with everything off, which is the
    /// safe direction to fail: nothing is contacted and nothing is watched until the user says so
    /// again.
    /// </remarks>
    public SyncSettings Load()
    {
        lock (_lock)
        {
            return Read() ?? new SyncSettings();
        }
    }

    /// <summary>Applies a change and saves it, as one step.</summary>
    public SyncSettings Update(Action<SyncSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        SyncSettings settings;

        lock (_lock)
        {
            settings = Read() ?? new SyncSettings();
            change(settings);
            Write(settings);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return settings;
    }

    private SyncSettings? Read()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(Path), Json);

            // Deserialization builds the dictionary with the default comparer; restore the one the
            // type is written against.
            if (settings is not null)
            {
                settings.WatchedFolders = new(settings.WatchedFolders, StringComparer.Ordinal);
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Sync settings could not be read; using defaults.");

            return null;
        }
    }

    private void Write(SyncSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var temporary = Path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));

        if (File.Exists(Path))
        {
            File.Replace(temporary, Path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, Path);
        }

        // Counts and switches only (P6). The API hash is a credential, and a folder list is a map of
        // where someone keeps their correspondence.
        _log.LogInformation(
            "Sync settings saved. Watched folder lists {Saves}, Telegram enabled {Telegram}.",
            settings.WatchedFolders.Count, settings.Telegram.Enabled);
    }
}
