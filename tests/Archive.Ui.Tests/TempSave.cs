using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Import.Synthetic;
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
        Suggestions = new MergeSuggestions(Database);
        Conversation = new PersonConversation(Database);
        Search = new ArchiveSearch(Database);
        Runner = new ImportRunner(Database, MediaStore);
    }

    internal ArchiveOptions Options { get; }
    internal Database Database { get; }
    internal IMediaStore MediaStore { get; }
    internal ArchiveQueries Queries { get; }
    internal IdentityMerger Merger { get; }
    internal MergeSuggestions Suggestions { get; }
    internal PersonConversation Conversation { get; }
    internal ArchiveSearch Search { get; }
    internal ImportRunner Runner { get; }

    /// <summary>
    /// Runs SQL against the save.
    /// </summary>
    /// <remarks>
    /// For the states an import cannot produce on its own — a second platform's account for a
    /// person who already has one, which is what a merge suggestion is about. Building that
    /// through two real imports would make the test about the importers instead.
    /// </remarks>
    internal void Execute(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A named export folder belonging to this save.</summary>
    internal string ExportFolder(string name)
    {
        var folder = Path.Combine(_directory, "exports", name);
        Directory.CreateDirectory(folder);

        return folder;
    }

    /// <summary>Writes a throwaway Telegram export and returns its folder.</summary>
    internal string WriteExport(string name, string json)
    {
        var folder = ExportFolder(name);
        File.WriteAllText(Path.Combine(folder, "result.json"), json);
        return folder;
    }

    /// <summary>Writes an export built by <see cref="TelegramExportBuilder"/> and returns its folder.</summary>
    internal string WriteExport(string name, TelegramExportBuilder export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return export.Write(ExportFolder(name));
    }

    internal static TelegramAccount Owner { get; } = TelegramAccount.User(777001, "Owner Synthetic");

    internal static TelegramAccount Sam { get; } = TelegramAccount.User(5001, "Sam Ruiz");

    internal static TelegramAccount Alex { get; } = TelegramAccount.User(5002, "Alex Novak");

    internal static TelegramAccount Marina { get; } = TelegramAccount.User(5003, "Марина Коваль");

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    /// <summary>The smallest export that carries a couple of messages from one contact.</summary>
    internal string WriteSampleExport(string name = "sample") => WriteExport(
        name,
        TelegramExportBuilder.Full()
            .Owner(777001, "Owner", "Synthetic")
            .Chat("Sam Ruiz", "personal_chat", 100, c => c
                .Message(1, At(1554221523), Sam, "the harbour was freezing")
                .Message(2, At(1554221600), Owner, "we should go back")));

    /// <summary>
    /// A group with three people talking in it, one of them twice in a row.
    /// </summary>
    /// <remarks>
    /// The consecutive pair is the point: a conversation view has to label the first message of a
    /// run and not the second, and a group where everyone alternates would pass either way.
    /// </remarks>
    internal string WriteGroupExport(string name = "group") => WriteExport(
        name,
        TelegramExportBuilder.Full()
            .Owner(777001, "Owner", "Synthetic")
            .Chat("Prague trip", "private_group", 200, c => c
                .Message(1, At(1551427200), Sam, "yeah exactly")
                .Message(2, At(1551427260), Sam, "the early train then")
                .Message(3, At(1551427320), Marina, "поезд в 07:40")
                .Message(4, At(1551427380), Owner, "booked")
                .Message(5, At(1551427440), Alex, "see you thursday")));

    /// <summary>
    /// Releases this save's pooled handles, and nobody else's.
    /// </summary>
    /// <remarks>
    /// Never <c>ClearAllPools</c>: it is process-wide, and xUnit runs test classes in parallel in
    /// one process, so it disposes the <c>sqlite3</c> handle of a connection another test is in
    /// the middle of using. That surfaced as an <see cref="ObjectDisposedException"/> from
    /// somewhere unrelated, roughly once every few full runs.
    /// </remarks>
    private void ClearOwnPool()
    {
        using var connection = new SqliteConnection(Database.ConnectionString);
        SqliteConnection.ClearPool(connection);
    }

    public void Dispose()
    {
        ClearOwnPool();

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
