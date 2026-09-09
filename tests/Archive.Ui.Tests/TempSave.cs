using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Media;
using Microsoft.Data.Sqlite;

namespace Archive.Ui.Tests;

/// <summary>A migrated save plus the services a page needs, in a throwaway directory.</summary>
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
        Queries = new ArchiveQueries(Database);
        Merger = new IdentityMerger(Database);
        Conversation = new PersonConversation(Database);
        Runner = new ImportRunner(Database, MediaStore);
    }

    internal ArchiveOptions Options { get; }
    internal Database Database { get; }
    internal IMediaStore MediaStore { get; }
    internal ArchiveQueries Queries { get; }
    internal IdentityMerger Merger { get; }
    internal PersonConversation Conversation { get; }
    internal ImportRunner Runner { get; }

    /// <summary>Writes a throwaway Telegram export and returns its folder.</summary>
    internal string WriteExport(string name, string json)
    {
        var folder = Path.Combine(_directory, "exports", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"), json);
        return folder;
    }

    /// <summary>The smallest export that carries a couple of messages from one contact.</summary>
    internal string WriteSampleExport(string name = "sample") => WriteExport(name, """
        {
          "personal_information": { "user_id": 777001, "first_name": "Kirill", "last_name": "Chekanov" },
          "chats": { "list": [
            { "name": "Sam Ruiz", "type": "personal_chat", "id": 100, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1554221523", "from": "Sam Ruiz", "from_id": "user5001",
                "text": "the harbour was freezing",
                "text_entities": [ { "type": "plain", "text": "the harbour was freezing" } ] },
              { "id": 2, "type": "message", "date_unixtime": "1554221600", "from": "Kirill", "from_id": "user777001",
                "text": "we should go back",
                "text_entities": [ { "type": "plain", "text": "we should go back" } ] }
            ] }
          ] }
        }
        """);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

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
