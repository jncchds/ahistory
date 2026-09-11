using System.Text.Json;
using Archive.Data;

namespace Archive.Ai.Search;

/// <summary>
/// Keyword and meaning together, ranked as one list (spec §5, ai-plan.md §10).
/// </summary>
/// <remarks>
/// <para>
/// Keyword search is the baseline and is not touched: it runs exactly as it does with AI off, and
/// this adds a second list beside it. The two are merged by reciprocal rank — each list votes by
/// position, not by score, because a bm25 number and a cosine number are not on any common scale.
/// </para>
/// <para>
/// Meaning finds sessions, and a result is a message, so a session found by meaning is shown as the
/// message in it that best matches what was asked — the one a keyword search also found, when there
/// is one, which is then marked as found both ways.
/// </para>
/// </remarks>
public sealed class HybridSearch(Database database, ArchiveSearch keyword, SemanticSearch semantic)
{
    /// <summary>The usual constant for reciprocal-rank fusion; it keeps the top of either list from dominating.</summary>
    private const double RankDamping = 60;

    /// <summary>How many sessions meaning contributes.</summary>
    private const int SemanticDepth = 40;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    private readonly ArchiveSearch _keyword = keyword ?? throw new ArgumentNullException(nameof(keyword));

    private readonly SemanticSearch _semantic = semantic ?? throw new ArgumentNullException(nameof(semantic));

    public SemanticAvailability Availability() => _semantic.Availability();

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, SearchFilter? filter = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        filter ??= new SearchFilter();

        var words = _keyword.Search(query, filter, limit);
        var meaning = filter.Provenance is null or "message"
            ? await _semantic.SearchAsync(query, SemanticDepth, cancellationToken).ConfigureAwait(false)
            : [];

        var sessionOf = SessionsOf(words.Select(h => h.MessageId));
        var scores = new Dictionary<long, (double Score, bool Keyword, bool Meaning)>();

        for (var i = 0; i < words.Count; i++)
        {
            scores[words[i].MessageId] = (1 / (RankDamping + i + 1), true, false);
        }

        for (var i = 0; i < meaning.Count; i++)
        {
            var vote = 1 / (RankDamping + i + 1);
            var session = meaning[i].SessionId;

            var matched = words.Where(h => sessionOf.TryGetValue(h.MessageId, out var s) && s == session).ToList();

            if (matched.Count > 0)
            {
                foreach (var hit in matched)
                {
                    var current = scores[hit.MessageId];
                    scores[hit.MessageId] = (current.Score + vote, true, true);
                }

                continue;
            }

            if (Representative(session, query, filter) is { } messageId && !scores.ContainsKey(messageId))
            {
                scores[messageId] = (vote, false, true);
            }
        }

        var ranked = scores.OrderByDescending(s => s.Value.Score).Take(limit).ToList();
        var byId = words.ToDictionary(h => h.MessageId);
        var found = Hits(ranked.Where(r => !byId.ContainsKey(r.Key)).Select(r => r.Key));

        return [.. ranked
            .Select(r =>
            {
                var hit = byId.TryGetValue(r.Key, out var k) ? k : found.GetValueOrDefault(r.Key);

                return hit is null
                    ? null
                    : hit with { FoundBy = r.Value.Keyword && r.Value.Meaning ? "both" : r.Value.Keyword ? "keyword" : "meaning" };
            })
            .OfType<SearchHit>()];
    }

    private Dictionary<long, string> SessionsOf(IEnumerable<long> messageIds)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, session_id FROM message
            WHERE id IN (SELECT value FROM json_each($ids)) AND session_id IS NOT NULL;
            """;

        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(messageIds.ToArray()));

        var sessions = new Dictionary<long, string>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions[reader.GetInt64(0)] = reader.GetString(1);
        }

        return sessions;
    }

    /// <summary>
    /// The message that stands for a session in the results: the one sharing most words with the
    /// query, or failing that the longest — within whatever the search is narrowed to.
    /// </summary>
    private long? Representative(string sessionId, string query, SearchFilter filter)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT m.id, m.plaintext
            FROM message AS m
            LEFT JOIN identity_person AS ip ON ip.identity_id = m.sender_identity_id
            WHERE m.session_id = $session AND m.kind = 'message' AND m.is_deleted = 0 AND m.plaintext <> ''
              AND ($thread IS NULL OR m.thread_id = $thread)
              AND ($person IS NULL OR ip.person_id = $person)
              AND ($from IS NULL OR m.sent_at_unix >= $from)
              AND ($to IS NULL OR m.sent_at_unix <= $to);
            """;

        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$thread", (object?)filter.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$person", (object?)filter.PersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)filter.FromUnix ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)filter.ToUnix ?? DBNull.Value);

        var terms = query.ToLowerInvariant()
            .Split([' ', ',', '.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToArray();

        long? best = null;
        var bestScore = (-1, -1);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var text = reader.GetString(1);
            var lower = text.ToLowerInvariant();
            var score = (terms.Count(t => lower.Contains(t, StringComparison.Ordinal)), text.Length);

            if (score.CompareTo(bestScore) > 0)
            {
                best = reader.GetInt64(0);
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Search hits for messages found only by meaning, with the start of each as its snippet.</summary>
    private Dictionary<long, SearchHit> Hits(IEnumerable<long> messageIds)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT m.id, m.uid, m.thread_id, t.title, i.display_name, ip.person_id,
                   ifnull(p.is_owner, 0), m.sent_at_utc, m.sent_at_unix, m.plaintext
            FROM message AS m
            LEFT JOIN thread AS t ON t.id = m.thread_id
            LEFT JOIN identity AS i ON i.id = m.sender_identity_id
            LEFT JOIN identity_person AS ip ON ip.identity_id = i.id
            LEFT JOIN person AS p ON p.id = ip.person_id
            WHERE m.id IN (SELECT value FROM json_each($ids));
            """;

        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(messageIds.ToArray()));

        var hits = new Dictionary<long, SearchHit>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var text = reader.GetString(9);

            hits[reader.GetInt64(0)] = new SearchHit(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) == 1,
                reader.GetString(7),
                reader.GetInt64(8),
                "message",
                [new SnippetSegment(text.Length > 200 ? text[..200] + "…" : text, false)]);
        }

        return hits;
    }
}
