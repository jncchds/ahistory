using System.Globalization;
using System.Text;
using Archive.Data;

namespace Archive.Ai.Extraction;

/// <summary>Someone who can be the subject of a fact in this window.</summary>
public sealed record WindowPerson(string Id, string Name, bool IsOwner);

/// <summary>One message, as the model is shown it.</summary>
public sealed record WindowMessage(long Id, string? SenderPersonId, string SenderName, long SentAtUnix, string Text);

/// <summary>A fact already believed about someone here, so the model can add to it rather than restate it.</summary>
public sealed record KnownFact(string Id, string Subject, string Predicate, string ObjectText, string ClaimText);

/// <summary>
/// One session, everything needed to read it, and nothing else.
/// </summary>
/// <remarks>
/// The unit of extraction (§6.3), and the boundary every citation is checked against: a message id
/// that is not in here is a hallucination or a mistake, and either way it does not get written.
/// </remarks>
public sealed record ExtractionWindow(
    string SessionId,
    string ThreadId,
    bool IsGroup,
    IReadOnlyList<WindowPerson> People,
    IReadOnlyList<WindowMessage> Messages,
    IReadOnlyList<KnownFact> Known)
{
    /// <summary>Every message id that may be cited.</summary>
    public HashSet<long> CitableIds { get; } = [.. Messages.Select(m => m.Id)];

    /// <summary>Who sent a message, as a person; null for an account nobody has been merged into.</summary>
    public string? SenderOf(long messageId) =>
        Messages.FirstOrDefault(m => m.Id == messageId)?.SenderPersonId;

    /// <summary>
    /// The transcript, as the model reads it.
    /// </summary>
    /// <remarks>
    /// Real message ids rather than positions in a list, so a citation the model writes is already
    /// the thing that gets stored — one fewer translation between what was said and what is
    /// recorded as having been said.
    /// </remarks>
    public string Transcript()
    {
        var text = new StringBuilder();

        foreach (var message in Messages)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(message.SentAtUnix)
                .UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            text.Append('[').Append(message.Id).Append("] ")
                .Append(when).Append(' ')
                .Append(message.SenderName).Append(": ")
                .AppendLine(message.Text);
        }

        return text.ToString();
    }
}

/// <summary>
/// Loads a session and its context.
/// </summary>
/// <remarks>
/// Everything a model is given about a session comes from here, which makes this the one place
/// that has to honour the exclusions: a person or thread marked <c>ai_excluded</c> is not read,
/// and with a hosted endpoint that is the difference between "not analysed" and "not sent".
/// </remarks>
public sealed class ExtractionWindows(Database database)
{
    /// <summary>How many already-known facts about the participants are shown as context (§6.3).</summary>
    private const int KnownFactLimit = 40;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>The window for a session, or null when it must not be read.</summary>
    public ExtractionWindow? Load(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var connection = _database.Open();

        string threadId;
        bool isGroup;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.id, t.kind, t.ai_excluded, coalesce(sm.ai_opt_out, 0)
                FROM session AS s
                JOIN thread AS t ON t.id = s.thread_id
                LEFT JOIN save_meta AS sm ON sm.id = 1
                WHERE s.id = $session;
                """;

            command.Parameters.AddWithValue("$session", sessionId);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            // Either flag is enough. A save that opted out is not read on any machine, and a
            // thread that was excluded is not read on this one.
            if (reader.GetInt64(2) == 1 || reader.GetInt64(3) == 1)
            {
                return null;
            }

            threadId = reader.GetString(0);
            isGroup = reader.GetString(1) != "dm";
        }

        // A direct conversation with someone left out is not read at all. Taking them off the
        // roster is not enough: their messages would still be the transcript, and still be sent.
        if (!isGroup && HasSomeoneLeftOut(connection, threadId))
        {
            return null;
        }

        var people = People(connection, threadId);

        if (people.Count == 0)
        {
            // Nobody the facts could be about. Extraction would have no subject to offer.
            return null;
        }

        var messages = Messages(connection, sessionId);

        return messages.Count == 0
            ? null
            : new ExtractionWindow(
                SessionId: sessionId,
                ThreadId: threadId,
                IsGroup: isGroup,
                People: people,
                Messages: messages,
                Known: KnownFacts(connection, people));
    }

    private static bool HasSomeoneLeftOut(Microsoft.Data.Sqlite.SqliteConnection connection, string threadId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM thread_participant AS tp
                JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
                JOIN person AS p ON p.id = ip.person_id
                WHERE tp.thread_id = $thread AND p.ai_excluded = 1);
            """;

        command.Parameters.AddWithValue("$thread", threadId);

        return command.ExecuteScalar() is long found && found == 1;
    }

    private static List<WindowPerson> People(Microsoft.Data.Sqlite.SqliteConnection connection, string threadId)
    {
        using var command = connection.CreateCommand();

        // The owner is included whether or not they are recorded as a participant: they are a
        // subject of facts in every conversation they are part of, and §7 profiles them through
        // the same pipeline as everyone else rather than a second one.
        command.CommandText = """
            SELECT DISTINCT p.id, p.display_name, p.is_owner
            FROM person AS p
            JOIN identity_person AS ip ON ip.person_id = p.id
            JOIN thread_participant AS tp ON tp.identity_id = ip.identity_id
            WHERE tp.thread_id = $thread AND p.ai_excluded = 0
            UNION
            SELECT p.id, p.display_name, p.is_owner
            FROM person AS p
            WHERE p.is_owner = 1 AND p.ai_excluded = 0;
            """;

        command.Parameters.AddWithValue("$thread", threadId);

        var people = new List<WindowPerson>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            people.Add(new WindowPerson(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1));
        }

        return people;
    }

    private static List<WindowMessage> Messages(Microsoft.Data.Sqlite.SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT m.id, ip.person_id, coalesce(p.display_name, i.display_name, '?'),
                   m.sent_at_unix, m.plaintext
            FROM message AS m
            LEFT JOIN identity AS i ON i.id = m.sender_identity_id
            LEFT JOIN identity_person AS ip ON ip.identity_id = i.id
            LEFT JOIN person AS p ON p.id = ip.person_id
            WHERE m.session_id = $session AND m.kind = 'message' AND m.is_deleted = 0
              -- In a group, the lines of someone left out are dropped before anything is sent.
              AND (p.id IS NULL OR p.ai_excluded = 0)
            ORDER BY m.sent_at_unix, m.id;
            """;

        command.Parameters.AddWithValue("$session", sessionId);

        var messages = new List<WindowMessage>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var text = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            if (string.IsNullOrWhiteSpace(text))
            {
                // A sticker or a photo with no caption. It is part of the session and part of its
                // timing, but there is nothing in it to cite, so it is not put in the transcript.
                continue;
            }

            messages.Add(new WindowMessage(
                Id: reader.GetInt64(0),
                SenderPersonId: reader.IsDBNull(1) ? null : reader.GetString(1),
                SenderName: reader.GetString(2),
                SentAtUnix: reader.GetInt64(3),
                Text: text));
        }

        return messages;
    }

    /// <summary>
    /// What is already believed about these people.
    /// </summary>
    /// <remarks>
    /// §6.3's context header. Without it the model restates the same fact from every session it
    /// appears in, and the merge step downstream has to clean up work that never needed doing.
    /// </remarks>
    private static List<KnownFact> KnownFacts(
        Microsoft.Data.Sqlite.SqliteConnection connection, List<WindowPerson> people)
    {
        using var command = connection.CreateCommand();

        var ids = string.Join(", ", people.Select((_, i) => $"$p{i}"));

        command.CommandText = $"""
            SELECT f.id, f.subject_person_id, f.predicate, f.object_text, f.claim_text
            FROM fact AS f
            WHERE f.subject_person_id IN ({ids})
              AND f.superseded_by IS NULL
              AND f.retracted_utc IS NULL
              AND f.source <> 'user_deleted'
            ORDER BY f.confidence DESC
            LIMIT {KnownFactLimit};
            """;

        for (var i = 0; i < people.Count; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", people[i].Id);
        }

        var facts = new List<KnownFact>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            facts.Add(new KnownFact(
                Id: reader.GetString(0),
                Subject: reader.GetString(1),
                Predicate: reader.GetString(2),
                ObjectText: reader.GetString(3),
                ClaimText: reader.GetString(4)));
        }

        return facts;
    }
}
