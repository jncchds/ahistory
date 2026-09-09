using Archive.Core;
using Microsoft.Data.Sqlite;

namespace Archive.Data.Tests;

/// <summary>
/// A real SQLite file in a throwaway directory, migrated and ready to use.
/// </summary>
/// <remarks>
/// Deliberately a file rather than <c>:memory:</c>. WAL mode, foreign key enforcement, cascade
/// behaviour and the migration runner itself all behave differently — or not at all — against an
/// in-memory database, and those are precisely the things these tests exist to check.
///
/// <see cref="SqliteConnection.ClearAllPools"/> on dispose is not optional on Windows: pooled
/// connections keep the file handle open, and the directory delete then fails with a sharing
/// violation that looks like a flaky test.
/// </remarks>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    internal TempDatabase(bool migrate = true)
    {
        _directory = Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        Options = new ArchiveOptions { DatabasePath = Path.Combine(_directory, "archive.db") };
        Database = new Database(Options);

        if (migrate)
        {
            Database.Migrate();
        }
    }

    internal ArchiveOptions Options { get; }

    internal Database Database { get; }

    internal SqliteConnection Open() => Database.Open();

    /// <summary>Runs statements that are not expected to return rows.</summary>
    internal void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs a query expected to return a single scalar.</summary>
    internal T? Scalar<T>(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle should not turn a passing test red. The temp directory is
            // disposable by definition and the OS will reclaim it.
        }
    }
}
