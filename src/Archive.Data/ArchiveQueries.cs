using Microsoft.Data.Sqlite;

namespace Archive.Data;

/// <summary>Headline counts for the overview.</summary>
public sealed record SaveSummary(
    long Messages,
    long Threads,
    long People,
    long Identities,
    long SyntheticIdentities,
    long Sources,
    long MediaFiles,
    string? OwnerName,
    string? LastImportUtc);

/// <summary>A person as the people list shows them.</summary>
public sealed record PersonRow(
    string Id,
    string DisplayName,
    bool IsOwner,
    long IdentityCount,
    long MessageCount);

/// <summary>A platform account, and the person it currently points at.</summary>
public sealed record IdentityRow(
    string Id,
    string DisplayName,
    string? Handle,
    string? SourceIdentityId,
    bool IsSynthetic,
    string PersonId,
    string PersonName,
    string Confidence,
    long MessageCount);

/// <summary>A conversation.</summary>
public sealed record ThreadRow(
    string Id,
    string Kind,
    string? Title,
    long MessageCount,
    long? LastMessageUnix);

/// <summary>One message, shaped for display.</summary>
public sealed record MessageRow(
    long Id,
    string Uid,
    string ThreadId,
    string? SenderName,
    bool FromOwner,
    string Kind,
    string? ServiceAction,
    string SentAtUtc,
    long SentAtUnix,
    string Plaintext,
    string? EntitiesJson,
    long MediaCount);

/// <summary>A page of messages, plus the cursor that continues it.</summary>
public sealed record MessagePage(IReadOnlyList<MessageRow> Messages, long? NextBeforeUnix, long? NextBeforeId)
{
    public bool HasMore => NextBeforeUnix is not null;
}

/// <summary>
/// Read-side queries over a save.
/// </summary>
/// <remarks>
/// Raw SQL rather than EF, for the same reason the committer uses it: these are the shapes the
/// app actually asks for, and expressing them directly keeps the index they depend on visible in
/// the query rather than implied by a translation.
/// </remarks>
public sealed class ArchiveQueries(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    public SaveSummary Summary()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM message),
                   (SELECT count(*) FROM thread),
                   (SELECT count(*) FROM person),
                   (SELECT count(*) FROM identity),
                   (SELECT count(*) FROM identity WHERE is_synthetic = 1),
                   (SELECT count(*) FROM import_source),
                   (SELECT count(*) FROM media),
                   (SELECT display_name FROM person WHERE is_owner = 1),
                   (SELECT max(finished_utc) FROM import WHERE status = 'completed');
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        return new SaveSummary(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    /// <summary>People, owner first, then by how much they said.</summary>
    public IReadOnlyList<PersonRow> People(string? search = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.display_name, p.is_owner,
                   (SELECT count(*) FROM identity_person ip WHERE ip.person_id = p.id),
                   (SELECT count(*) FROM message m
                    JOIN identity_person ip2 ON ip2.identity_id = m.sender_identity_id
                    WHERE ip2.person_id = p.id)
            FROM person p
            WHERE $search IS NULL OR p.display_name LIKE '%' || $search || '%'
            ORDER BY p.is_owner DESC, 5 DESC, p.display_name;
            """;
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search);

        var people = new List<PersonRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            people.Add(new PersonRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1,
                reader.GetInt64(3), reader.GetInt64(4)));
        }

        return people;
    }

    /// <summary>Every platform account and who it is currently attributed to.</summary>
    public IReadOnlyList<IdentityRow> Identities(bool syntheticOnly = false)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.display_name, i.handle, i.source_identity_id, i.is_synthetic,
                   ip.person_id, p.display_name, ip.confidence,
                   (SELECT count(*) FROM message m WHERE m.sender_identity_id = i.id)
            FROM identity i
            JOIN identity_person ip ON ip.identity_id = i.id
            JOIN person p ON p.id = ip.person_id
            WHERE $syntheticOnly = 0 OR i.is_synthetic = 1
            ORDER BY 9 DESC, i.display_name;
            """;
        command.Parameters.AddWithValue("$syntheticOnly", syntheticOnly ? 1 : 0);

        var identities = new List<IdentityRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            identities.Add(new IdentityRow(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) == 1, reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetInt64(8)));
        }

        return identities;
    }

    public IReadOnlyList<ThreadRow> Threads()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.kind, t.title,
                   count(m.id), max(m.sent_at_unix)
            FROM thread t
            LEFT JOIN message m ON m.thread_id = t.id
            GROUP BY t.id, t.kind, t.title
            ORDER BY 5 DESC NULLS LAST, t.title;
            """;

        var threads = new List<ThreadRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            threads.Add(new ThreadRow(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return threads;
    }

    /// <summary>
    /// A page of one thread's messages, newest first.
    /// </summary>
    /// <remarks>
    /// Keyset, not OFFSET: at half a million rows an offset scan re-reads everything it skipped,
    /// and pages drift when a background import inserts underneath the reader. The cursor is
    /// <c>(sent_at_unix, id)</c> — the id tie-break is not optional, because Telegram timestamps
    /// collide at second granularity constantly and a timestamp-only cursor drops or repeats
    /// messages at every page boundary.
    ///
    /// Served as a descending scan of ix_message_thread_time.
    /// </remarks>
    public MessagePage ThreadMessages(string threadId, int limit = 100, long? beforeUnix = null, long? beforeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.uid, m.thread_id, i.display_name,
                   ifnull(p.is_owner, 0), m.kind, m.service_action, m.sent_at_utc, m.sent_at_unix,
                   m.plaintext, m.entities_json,
                   (SELECT count(*) FROM message_media mm WHERE mm.message_id = m.id)
            FROM message m
            LEFT JOIN identity i ON i.id = m.sender_identity_id
            LEFT JOIN identity_person ip ON ip.identity_id = i.id
            LEFT JOIN person p ON p.id = ip.person_id
            WHERE m.thread_id = $thread
              AND ($beforeUnix IS NULL
                   OR m.sent_at_unix < $beforeUnix
                   OR (m.sent_at_unix = $beforeUnix AND m.id < $beforeId))
            ORDER BY m.sent_at_unix DESC, m.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$beforeUnix", beforeUnix ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$beforeId", beforeId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var messages = new List<MessageRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            messages.Add(new MessageRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) == 1,
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7), reader.GetInt64(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt64(11)));
        }

        // A short page means the end; a full one hands back a cursor even if the next page is
        // empty, which costs one query and avoids a count.
        var last = messages.Count == limit ? messages[^1] : null;

        return new MessagePage(messages, last?.SentAtUnix, last?.Id);
    }
}
