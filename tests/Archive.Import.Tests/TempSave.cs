using Archive.Core;
using Archive.Data;
using Archive.Import.Synthetic;
using Archive.Media;
using Microsoft.Data.Sqlite;

namespace Archive.Import.Tests;

/// <summary>A migrated save with its media folder, in a throwaway directory.</summary>
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
    }

    internal ArchiveOptions Options { get; }

    internal Database Database { get; }

    internal IMediaStore MediaStore { get; }

    internal ImportRunner Runner => new(Database, MediaStore);

    internal ImportRunner RunnerWith(IMediaStore store) => new(Database, store);

    internal ImportStats Import(string exportFolder) => Runner.Run(exportFolder);

    /// <summary>
    /// A named export folder belonging to this save.
    /// </summary>
    /// <remarks>
    /// Named rather than randomized, and inside the save's own directory, because a folder's name
    /// is part of the source id an export that will not identify its account falls back to
    /// (<c>ImportSourceResolver.FolderSourceId</c>). Asking for the same name twice must therefore
    /// give the same folder, or a test that re-imports "the same export" would be importing a
    /// different one — while two tests still never share a folder.
    /// </remarks>
    internal string ExportFolder(string name)
    {
        var folder = Path.Combine(_directory, "exports", name);
        Directory.CreateDirectory(folder);

        return folder;
    }

    internal string Export(string name, TelegramExportBuilder export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return export.Write(ExportFolder(name));
    }

    internal string Export(string name, HangoutsExportBuilder export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return export.Write(ExportFolder(name));
    }

    internal string Export(string name, VkExportBuilder export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return export.Write(ExportFolder(name));
    }

    internal ImportStats Import(string name, TelegramExportBuilder export) => Import(Export(name, export));

    internal string Digest() => DatabaseDigest.Of(Database);

    internal void Execute(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Import run ids, oldest first.</summary>
    internal string[] ImportIdsInOrder()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM import ORDER BY started_utc, rowid;";

        var ids = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return [.. ids];
    }

    internal T? Scalar<T>(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>Files actually in the media store, ignoring staging.</summary>
    internal string[] MediaFiles() =>
        Directory.Exists(MediaStore.Root)
            ? [.. Directory.EnumerateFiles(MediaStore.Root, "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains(".staging", StringComparison.Ordinal))]
            : [];

    /// <summary>
    /// Writes a throwaway export folder from a JSON string.
    /// </summary>
    /// <remarks>
    /// Used for the scenarios where the *relationship between two exports* is the point — an edit
    /// between them, or one being a superset of the other. Committing those as fixture pairs
    /// would hide the thing under test in two files that have to be read side by side.
    /// </remarks>
    internal string WriteExport(string name, string json)
    {
        var folder = Path.Combine(_directory, "exports", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"), json);
        return folder;
    }

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
