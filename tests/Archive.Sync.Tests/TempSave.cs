using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Media;
using Microsoft.Data.Sqlite;

namespace Archive.Sync.Tests;

/// <summary>A migrated save, its media folder, and a settings directory of its own.</summary>
/// <remarks>
/// The settings directory matters as much as the save here: <c>sync.json</c> is per machine, and a
/// test that wrote to the real one would rewrite what the person running the suite has watched.
/// </remarks>
internal sealed class TempSave : IDisposable
{
    private readonly string _directory;

    internal TempSave()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        Options = new ArchiveOptions { DatabasePath = Path.Combine(_directory, "archive.db") };
        Database = new Database(Options);
        Database.Migrate();
        MediaStore = new FileSystemMediaStore(Options);
        Runner = new ImportRunner(Database, MediaStore);
        Settings = new SyncSettingsStore(Path.Combine(_directory, "settings"));
    }

    internal ArchiveOptions Options { get; }

    internal Database Database { get; }

    internal IMediaStore MediaStore { get; }

    internal ImportRunner Runner { get; }

    internal SyncSettingsStore Settings { get; }

    internal string SavePath => Database.DatabasePath;

    internal FolderWatcher Watcher(TimeSpan? quiet = null, TimeSpan? poll = null) =>
        new(Runner, Settings, SavePath, logger: null, quiet, poll);

    /// <summary>A folder holding a Telegram export with the given messages.</summary>
    internal string Export(string name, params (int Id, string Text)[] messages)
    {
        var folder = Path.Combine(_directory, "exports", name);
        Directory.CreateDirectory(folder);

        var lines = messages.Select(m => $$"""
            { "id": {{m.Id}}, "type": "message", "date_unixtime": "155422152{{m.Id}}",
              "from_id": "user5001", "from": "Sam", "text": "{{m.Text}}",
              "text_entities": [ { "type": "plain", "text": "{{m.Text}}" } ] }
            """);

        File.WriteAllText(
            Path.Combine(folder, "result.json"),
            $$"""
              { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [ {{string.Join(",", lines)}} ] }
              """);

        return folder;
    }

    /// <summary>A folder that is recognized as a Telegram export and then fails to read.</summary>
    internal string BrokenExport(string name)
    {
        var folder = Path.Combine(_directory, "exports", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"), """{ "name": "Sam", "messages": [ { """);

        return folder;
    }

    internal string EmptyFolder(string name)
    {
        var folder = Path.Combine(_directory, name);
        Directory.CreateDirectory(folder);

        return folder;
    }

    internal T? Scalar<T>(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public void Dispose()
    {
        using (var connection = new SqliteConnection(Database.ConnectionString))
        {
            // Never ClearAllPools: it is process-wide, and test classes run in parallel.
            SqliteConnection.ClearPool(connection);
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp directory; the OS will reclaim it.
        }
    }
}
