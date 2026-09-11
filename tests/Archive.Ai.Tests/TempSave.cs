using Archive.Core;
using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Ai.Tests;

/// <summary>
/// A real, migrated SQLite file in a throwaway directory.
/// </summary>
/// <remarks>
/// A file rather than <c>:memory:</c>, for the reasons the data tests give: WAL, foreign keys and
/// cascade behaviour all differ. Pooled handles are released with
/// <see cref="SqliteConnection.ClearPool"/> and never <c>ClearAllPools</c>, which is process-wide
/// and would dispose a connection another test class is using.
/// </remarks>
internal sealed class TempSave : IDisposable
{
    private readonly string _directory;

    internal TempSave()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var options = new ArchiveOptions { DatabasePath = Path.Combine(_directory, "archive.db") };

        Database = new Database(options);
        Database.Migrate();

        Interactions = new AiInteractions(Database);
    }

    internal Database Database { get; }

    internal AiInteractions Interactions { get; }

    /// <summary>
    /// Puts a thread with messages into the save, at the times given.
    /// </summary>
    /// <remarks>
    /// Written in SQL rather than through an importer. What the segmenter is about is where the
    /// gaps in a thread fall, and building that through a real export would make these tests about
    /// the exporter's date handling instead — while making a four-hour gap something to encode in
    /// a fixture rather than something to state.
    /// </remarks>
    internal void Seed(string threadId, params (long Unix, string Text)[] messages)
    {
        using var connection = Database.Open();
        using var transaction = connection.BeginTransaction();

        Run(connection, """
            INSERT OR IGNORE INTO import_source (id, platform, created_utc)
                VALUES ('src', 'telegram', '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO import (
                id, source_id, platform, source_path, source_fingerprint,
                importer_version, status, started_utc)
                VALUES ('imp', 'src', 'telegram', '/tmp', 'fp', '1', 'completed',
                        '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO person (id, display_name, is_owner, created_utc)
                VALUES ('p_them', 'Them', 0, '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO identity (
                id, platform, source_identity_id, display_name, first_import_id, created_utc)
                VALUES ('i_them', 'telegram', 'them', 'Them', 'imp', '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO identity_person (identity_id, person_id, confidence, linked_utc)
                VALUES ('i_them', 'p_them', 'seed', '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO person (id, display_name, is_owner, created_utc)
                VALUES ('p_me', 'You', 1, '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO identity (
                id, platform, source_identity_id, display_name, first_import_id, created_utc)
                VALUES ('i_me', 'telegram', 'me', 'You', 'imp', '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO identity_person (identity_id, person_id, confidence, linked_utc)
                VALUES ('i_me', 'p_me', 'seed', '2026-01-01T00:00:00.0000000Z');
            """);

        Run(connection, $"""
            INSERT OR IGNORE INTO thread (
                id, platform, source_thread_id, kind, first_import_id, created_utc)
                VALUES ('{threadId}', 'telegram', '{threadId}', 'dm', 'imp',
                        '2026-01-01T00:00:00.0000000Z');
            INSERT OR IGNORE INTO thread_participant (thread_id, identity_id, first_seen_unix)
                VALUES ('{threadId}', 'i_them', 0);
            """);

        // Continues where the last call left off, so seeding a thread twice — which is how a
        // later import is simulated — does not collide on uid.
        var n = Count($"SELECT count(*) FROM message WHERE thread_id = '{threadId}';");

        foreach (var (unix, text) in messages)
        {
            using var command = connection.CreateCommand();

            command.CommandText = """
                INSERT INTO message (
                    uid, thread_id, sender_identity_id, kind, sent_at_utc, sent_at_unix,
                    plaintext, content_hash, first_import_id, importer_version)
                VALUES ($uid, $thread, 'i_them', 'message', $utc, $unix, $text, $hash, 'imp', '1');
                """;

            command.Parameters.AddWithValue("$uid", $"{threadId}:{n++}");
            command.Parameters.AddWithValue("$thread", threadId);
            command.Parameters.AddWithValue(
                "$utc",
                DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$unix", unix);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$hash", $"{threadId}:{n}");

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs statements that are not expected to return rows.</summary>
    internal void Execute(string sql)
    {
        using var connection = Database.Open();

        Run(connection, sql);
    }

    internal long Count(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal string? Text(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = command.ExecuteScalar();

        return value is null or DBNull ? null : (string)value;
    }

    public void Dispose()
    {
        using (var connection = new SqliteConnection(Database.ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
