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
/// Clearing the pool on dispose is not optional on Windows: pooled connections keep the file
/// handle open, and the directory delete then fails with a sharing violation that looks like a
/// flaky test. It must be <see cref="SqliteConnection.ClearPool"/> and not
/// <c>ClearAllPools</c> — see <see cref="ClearOwnPool"/>.
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

    /// <summary>
    /// Releases this database's pooled handles, and nobody else's.
    /// </summary>
    /// <remarks>
    /// <c>ClearAllPools</c> is process-wide, and xUnit runs test classes in parallel in one
    /// process. Disposing one temp database was therefore disposing the underlying
    /// <c>sqlite3</c> handle of a connection another test was in the middle of using, which
    /// surfaced as <see cref="ObjectDisposedException"/> from somewhere unrelated, roughly once
    /// every few full runs — the kind of failure that gets re-run until it passes and never
    /// diagnosed. <see cref="SqliteConnection.ClearPool"/> is scoped to one connection string.
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
            // A leaked handle should not turn a passing test red. The temp directory is
            // disposable by definition and the OS will reclaim it.
        }
    }
}
