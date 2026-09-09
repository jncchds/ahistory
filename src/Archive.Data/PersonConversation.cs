using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Archive.Data;

/// <summary>One message in a person's continuous conversation.</summary>
/// <param name="Origin">
/// <c>dm</c> when it came from a conversation with just this person, <c>group</c> when they said
/// it somewhere else. §4: a group line read on its own is often nonsense, so the two are not
/// presented alike.
/// </param>
public sealed record PersonMessageRow(
    long Id,
    string Uid,
    string ThreadId,
    string? ThreadTitle,
    string Origin,
    string? SenderName,
    bool FromOwner,
    string Kind,
    string? ServiceAction,
    string SentAtUtc,
    long SentAtUnix,
    string Plaintext,
    string? EntitiesJson,
    long MediaCount)
{
    public bool IsFromGroup => Origin == "group";
}

public sealed record PersonMessagePage(
    IReadOnlyList<PersonMessageRow> Messages,
    long? NextBeforeUnix,
    long? NextBeforeId)
{
    public bool HasMore => NextBeforeUnix is not null;
}

/// <summary>
/// The per-person continuous conversation (§4).
/// </summary>
/// <remarks>
/// <para>
/// Everything a person said to you, and everything you said to them, across every thread and
/// eventually every platform, in one time-ordered stream: their direct conversations in full,
/// plus the messages they sent in groups.
/// </para>
/// <para>
/// Group messages are <em>not</em> copied per participant. They live once in their real thread,
/// and this is a query over them — which is what makes the archive's size a function of what was
/// said rather than of how many people heard it.
/// </para>
/// </remarks>
public sealed class PersonConversation(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>
    /// A page of one person's conversation, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built as one index-range scan per source — per direct thread, and per identity for their
    /// group messages — with the keyset predicate and the LIMIT pushed <em>inside</em> each arm.
    /// A single outer WHERE and LIMIT over a UNION ALL makes SQLite materialize every arm in
    /// full before discarding almost all of it; at half a million rows that is the difference
    /// between a page and a pause.
    /// </para>
    /// <para>
    /// The merged result is sorted once over at most (arms × limit) rows. That small sort is
    /// unavoidable — SQLite will not merge pre-sorted UNION ALL arms — but it is bounded by the
    /// page size rather than by the size of the archive.
    /// </para>
    /// </remarks>
    public PersonMessagePage Page(string personId, int limit = 100, long? beforeUnix = null, long? beforeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();

        var identities = IdentitiesOf(connection, personId);

        if (identities.Count == 0)
        {
            return new PersonMessagePage([], null, null);
        }

        var directThreads = DirectThreadsOf(connection, identities);
        var (sql, parameters) = BuildQuery(identities, directThreads, beforeUnix, beforeId);

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.Parameters.AddWithValue("$limit", limit);

        var messages = new List<PersonMessageRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            messages.Add(new PersonMessageRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) == 1,
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetInt64(13)));
        }

        var last = messages.Count == limit ? messages[^1] : null;

        return new PersonMessagePage(messages, last?.SentAtUnix, last?.Id);
    }

    /// <summary>
    /// The query plan for a page, so a test can assert on it.
    /// </summary>
    /// <remarks>
    /// A sequential scan is invisible on a fixture and ruinous on a real archive, so the plan is
    /// checked rather than assumed. Exposed here because the SQL is assembled at runtime from the
    /// person's own threads and identities — there is no static query text to inspect.
    /// </remarks>
    internal IReadOnlyList<string> ExplainPage(string personId, int limit = 100)
    {
        using var connection = _database.Open();

        var identities = IdentitiesOf(connection, personId);
        var directThreads = DirectThreadsOf(connection, identities);
        var (sql, parameters) = BuildQuery(identities, directThreads, null, null);

        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.Parameters.AddWithValue("$limit", limit);

        var plan = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            plan.Add(reader.GetString(reader.FieldCount - 1));
        }

        return plan;
    }

    /// <summary>
    /// The messages either side of one message, in its own thread.
    /// </summary>
    /// <remarks>
    /// §4: "yeah exactly" means nothing on its own. When a group line appears in someone's
    /// conversation, this is what fills in what it was a reply to.
    /// </remarks>
    public IReadOnlyList<PersonMessageRow> Context(long messageId, int radius = 8)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        // Two bounded keyset scans outward from the anchor, both served by ix_message_thread_time.
        command.CommandText = $"""
            WITH anchor AS (
                SELECT thread_id, sent_at_unix, id FROM message WHERE id = $id
            ),
            window AS (
                -- Two bounded scans outward from the anchor. Each is wrapped because SQLite
                -- rejects ORDER BY and LIMIT written directly inside a UNION branch.
                SELECT * FROM (
                    SELECT m.id FROM message m, anchor a
                    WHERE m.thread_id = a.thread_id
                      AND (m.sent_at_unix < a.sent_at_unix
                           OR (m.sent_at_unix = a.sent_at_unix AND m.id <= a.id))
                    ORDER BY m.sent_at_unix DESC, m.id DESC
                    LIMIT $radius + 1
                )
                UNION
                SELECT * FROM (
                    SELECT m.id FROM message m, anchor a
                    WHERE m.thread_id = a.thread_id
                      AND (m.sent_at_unix > a.sent_at_unix
                           OR (m.sent_at_unix = a.sent_at_unix AND m.id > a.id))
                    ORDER BY m.sent_at_unix ASC, m.id ASC
                    LIMIT $radius
                )
            )
            {Projection("'group'")}
            WHERE m.id IN (SELECT id FROM window)
            ORDER BY m.sent_at_unix ASC, m.id ASC;
            """;

        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$radius", radius);

        var messages = new List<PersonMessageRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            messages.Add(new PersonMessageRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) == 1, reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9), reader.GetInt64(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetInt64(13)));
        }

        return messages;
    }

    /// <summary>The SQL that turns message ids into displayable rows.</summary>
    private static string Projection(string origin) => $"""
        SELECT m.id, m.uid, m.thread_id, t.title, {origin} AS origin,
               i.display_name, ifnull(p.is_owner, 0), m.kind, m.service_action,
               m.sent_at_utc, m.sent_at_unix, m.plaintext, m.entities_json,
               (SELECT count(*) FROM message_media mm WHERE mm.message_id = m.id)
        FROM message m
        LEFT JOIN thread t ON t.id = m.thread_id
        LEFT JOIN identity i ON i.id = m.sender_identity_id
        LEFT JOIN identity_person ip ON ip.identity_id = i.id
        LEFT JOIN person p ON p.id = ip.person_id
        """;

    /// <summary>
    /// Builds the union: one arm per direct thread, one per identity for group messages.
    /// </summary>
    /// <remarks>
    /// The arm count is bounded by how many accounts a person has and how many direct threads
    /// they appear in — a handful in practice, not a function of archive size. The SQL is
    /// assembled from ids read out of the database rather than interpolated user input, and every
    /// value is still bound as a parameter.
    /// </remarks>
    private static (string Sql, List<(string Name, object Value)> Parameters) BuildQuery(
        IReadOnlyList<string> identities,
        IReadOnlyList<string> directThreads,
        long? beforeUnix,
        long? beforeId)
    {
        var parameters = new List<(string, object)>();
        var arms = new List<string>();

        var keyset = beforeUnix is null
            ? string.Empty
            : " AND (m.sent_at_unix < $beforeUnix OR (m.sent_at_unix = $beforeUnix AND m.id < $beforeId))";

        if (beforeUnix is not null)
        {
            parameters.Add(("$beforeUnix", beforeUnix.Value));
            parameters.Add(("$beforeId", beforeId ?? long.MaxValue));
        }

        // Direct threads: everything in them, both sides of the conversation.
        for (var i = 0; i < directThreads.Count; i++)
        {
            var name = $"$thread{i.ToString(CultureInfo.InvariantCulture)}";
            parameters.Add((name, directThreads[i]));

            // Each arm is wrapped: SQLite rejects ORDER BY and LIMIT written directly inside a
            // UNION ALL branch, and the whole point is to bound each branch before merging.
            arms.Add($"""
                SELECT * FROM (
                    SELECT m.id, m.sent_at_unix, 'dm' AS origin
                    FROM message m
                    WHERE m.thread_id = {name}{keyset}
                    ORDER BY m.sent_at_unix DESC, m.id DESC
                    LIMIT $limit
                )
                """);
        }

        // Group messages: only the ones this person sent. What was said around them is context,
        // fetched on demand rather than folded into the stream.
        var excluded = directThreads.Count == 0
            ? string.Empty
            : $" AND m.thread_id NOT IN ({string.Join(", ", Enumerable.Range(0, directThreads.Count).Select(i => $"$thread{i}"))})";

        for (var i = 0; i < identities.Count; i++)
        {
            var name = $"$identity{i.ToString(CultureInfo.InvariantCulture)}";
            parameters.Add((name, identities[i]));

            arms.Add($"""
                SELECT * FROM (
                    SELECT m.id, m.sent_at_unix, 'group' AS origin
                    FROM message m
                    WHERE m.sender_identity_id = {name}{excluded}{keyset}
                    ORDER BY m.sent_at_unix DESC, m.id DESC
                    LIMIT $limit
                )
                """);
        }

        var union = string.Join("\n  UNION ALL\n", arms);

        var sql = new StringBuilder()
            .AppendLine("WITH merged AS (")
            .AppendLine(union)
            .AppendLine(")")
            .AppendLine(Projection("x.origin"))
            .AppendLine("JOIN merged x ON x.id = m.id")
            .AppendLine("ORDER BY m.sent_at_unix DESC, m.id DESC")
            .AppendLine("LIMIT $limit;")
            .ToString();

        return (sql, parameters);
    }

    private static List<string> IdentitiesOf(SqliteConnection connection, string personId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT identity_id FROM identity_person WHERE person_id = $person;";
        command.Parameters.AddWithValue("$person", personId);

        var identities = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            identities.Add(reader.GetString(0));
        }

        return identities;
    }

    /// <summary>
    /// Threads that are a conversation with this person rather than a room they were in.
    /// </summary>
    private static List<string> DirectThreadsOf(SqliteConnection connection, IReadOnlyList<string> identities)
    {
        using var command = connection.CreateCommand();

        var names = Enumerable.Range(0, identities.Count).Select(i => $"$i{i}").ToArray();

        command.CommandText = $"""
            SELECT DISTINCT t.id
            FROM thread t
            JOIN thread_participant tp ON tp.thread_id = t.id
            WHERE t.kind IN ('dm', 'saved')
              AND tp.identity_id IN ({string.Join(", ", names)});
            """;

        for (var i = 0; i < identities.Count; i++)
        {
            command.Parameters.AddWithValue(names[i], identities[i]);
        }

        var threads = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            threads.Add(reader.GetString(0));
        }

        return threads;
    }
}
