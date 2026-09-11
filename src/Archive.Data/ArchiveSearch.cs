using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Archive.Data;

/// <summary>A run of snippet text, matched or not.</summary>
public sealed record SnippetSegment(string Text, bool IsMatch);

/// <summary>One search hit.</summary>
/// <param name="Provenance">
/// §3: whether this text is the message itself or something derived from it — a transcript, OCR
/// of a screenshot. A confident transcript, a shaky one and a message are three different levels
/// of trust, and a result that hides which it is turns a mishearing into biography.
/// </param>
public sealed record SearchHit(
    long MessageId,
    string Uid,
    string ThreadId,
    string? ThreadTitle,
    string? SenderName,
    string? PersonId,
    bool FromOwner,
    string SentAtUtc,
    long SentAtUnix,
    string Provenance,
    IReadOnlyList<SnippetSegment> Snippet)
{
    /// <summary>
    /// Which half of a hybrid search found this: "keyword", "meaning" or "both"; null for plain
    /// keyword search.
    /// </summary>
    /// <remarks>
    /// A hit found by meaning with no word in common is the interesting case and also the one most
    /// likely to be nonsense, so it has to be recognisable at a glance (ai-plan.md §10).
    /// </remarks>
    public string? FoundBy { get; init; }
}

/// <summary>Narrows a search. Every field is optional.</summary>
public sealed record SearchFilter(
    string? ThreadId = null,
    string? PersonId = null,
    long? FromUnix = null,
    long? ToUnix = null,
    string? Provenance = null);

/// <summary>
/// Full-text search over the archive (§5).
/// </summary>
/// <remarks>
/// Keyword search is the baseline capability and works with no AI component present at all
/// (AGENTS.md P1). Semantic and hybrid ranking arrive later as an improvement on this, never as
/// what makes it function.
/// </remarks>
public sealed class ArchiveSearch(Database database)
{
    /// <summary>
    /// Markers wrapped around matched text by <c>snippet()</c>.
    /// </summary>
    /// <remarks>
    /// ASCII control characters, because the result has to be split on them afterwards and any
    /// printable choice is something a message could contain. STX and ETX cannot appear in text
    /// Telegram exported. <see cref="ParseSnippet"/> degrades to plain text if they somehow do.
    /// </remarks>
    private const char MatchStart = (char)2;
    private const char MatchEnd = (char)3;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>
    /// Searches the archive, best matches first.
    /// </summary>
    /// <remarks>
    /// Ranked by bm25, which returns negative scores where more negative is a better match — so
    /// the ordering is ascending, and getting that backwards silently returns the worst results
    /// first.
    /// </remarks>
    public IReadOnlyList<SearchHit> Search(
        string? query,
        SearchFilter? filter = null,
        int limit = 200,
        bool expandPrefixes = true)
    {
        var match = FtsQueryBuilder.Build(query, expandPrefixes);

        if (match is null)
        {
            return [];
        }

        filter ??= new SearchFilter();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT m.id, m.uid, m.thread_id, t.title, i.display_name, ip.person_id,
                   ifnull(p.is_owner, 0), m.sent_at_utc, m.sent_at_unix, sd.provenance,
                   snippet(search_fts, 0, '{MatchStart}', '{MatchEnd}', '…', 12)
            FROM search_fts
            JOIN search_document sd ON sd.id = search_fts.rowid
            -- A transcript or the text in an image is found through the message it came with (§3).
            LEFT JOIN derived_artifact da ON da.id = sd.derived_artifact_id
            JOIN message m ON m.id = coalesce(sd.message_id, da.source_message_id)
            LEFT JOIN thread t ON t.id = m.thread_id
            LEFT JOIN identity i ON i.id = m.sender_identity_id
            LEFT JOIN identity_person ip ON ip.identity_id = i.id
            LEFT JOIN person p ON p.id = ip.person_id
            WHERE search_fts MATCH $match
              AND ($threadId IS NULL OR m.thread_id = $threadId)
              AND ($personId IS NULL OR ip.person_id = $personId)
              AND ($fromUnix IS NULL OR m.sent_at_unix >= $fromUnix)
              AND ($toUnix IS NULL OR m.sent_at_unix <= $toUnix)
              AND ($provenance IS NULL OR sd.provenance = $provenance)
            ORDER BY bm25(search_fts, 10.0)
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$threadId", (object?)filter.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$personId", (object?)filter.PersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fromUnix", (object?)filter.FromUnix ?? DBNull.Value);
        command.Parameters.AddWithValue("$toUnix", (object?)filter.ToUnix ?? DBNull.Value);
        command.Parameters.AddWithValue("$provenance", (object?)filter.Provenance ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var hits = new List<SearchHit>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            hits.Add(new SearchHit(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) == 1,
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetString(9),
                ParseSnippet(reader.GetString(10))));
        }

        return hits;
    }

    /// <summary>How many messages a query matches, up to <paramref name="cap"/>.</summary>
    /// <param name="cap">
    /// Stop counting here. A word appearing in a large share of the archive otherwise costs a
    /// second full pass over every match — measured at 100 ms on 495k messages — to produce a
    /// number nobody reads precisely. "1,000+" says the same thing for a fraction of the work,
    /// and below the cap the count is exact.
    /// </param>
    /// <returns>The count, and whether it is exact or was cut off at the cap.</returns>
    public (long Count, bool IsExact) Count(
        string? query, SearchFilter? filter = null, int cap = 1000, bool expandPrefixes = true)
    {
        var match = FtsQueryBuilder.Build(query, expandPrefixes);

        if (match is null)
        {
            return (0, true);
        }

        filter ??= new SearchFilter();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        // The LIMIT is inside, so SQLite stops matching once enough rows are found rather than
        // counting them all and discarding the total.
        command.CommandText = """
            SELECT count(*) FROM (
                SELECT 1
                FROM search_fts
                JOIN search_document sd ON sd.id = search_fts.rowid
                LEFT JOIN derived_artifact da ON da.id = sd.derived_artifact_id
                JOIN message m ON m.id = coalesce(sd.message_id, da.source_message_id)
                LEFT JOIN identity i ON i.id = m.sender_identity_id
                LEFT JOIN identity_person ip ON ip.identity_id = i.id
                WHERE search_fts MATCH $match
                  AND ($threadId IS NULL OR m.thread_id = $threadId)
                  AND ($personId IS NULL OR ip.person_id = $personId)
                  AND ($fromUnix IS NULL OR m.sent_at_unix >= $fromUnix)
                  AND ($toUnix IS NULL OR m.sent_at_unix <= $toUnix)
                  AND ($provenance IS NULL OR sd.provenance = $provenance)
                LIMIT $cap
            );
            """;

        command.Parameters.AddWithValue("$cap", cap);

        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$threadId", (object?)filter.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$personId", (object?)filter.PersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fromUnix", (object?)filter.FromUnix ?? DBNull.Value);
        command.Parameters.AddWithValue("$toUnix", (object?)filter.ToUnix ?? DBNull.Value);
        command.Parameters.AddWithValue("$provenance", (object?)filter.Provenance ?? DBNull.Value);

        var count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        return (count, count < cap);
    }

    /// <summary>
    /// Splits snippet text into matched and unmatched runs.
    /// </summary>
    /// <remarks>
    /// Deliberately tolerant: unbalanced or stray markers produce plain text rather than an
    /// exception. A search box that throws on an unusual message is worse than one that renders
    /// it without highlighting.
    /// </remarks>
    internal static IReadOnlyList<SnippetSegment> ParseSnippet(string snippet)
    {
        if (string.IsNullOrEmpty(snippet))
        {
            return [];
        }

        var segments = new List<SnippetSegment>();
        var index = 0;

        while (index < snippet.Length)
        {
            var start = snippet.IndexOf(MatchStart, index);

            if (start < 0)
            {
                Add(segments, snippet[index..], isMatch: false);
                break;
            }

            Add(segments, snippet[index..start], isMatch: false);

            var end = snippet.IndexOf(MatchEnd, start + 1);

            if (end < 0)
            {
                // An opening marker with no close: take the rest as matched rather than losing it.
                Add(segments, snippet[(start + 1)..], isMatch: true);
                break;
            }

            Add(segments, snippet[(start + 1)..end], isMatch: true);
            index = end + 1;
        }

        return segments;
    }

    private static void Add(List<SnippetSegment> segments, string text, bool isMatch)
    {
        if (text.Length > 0)
        {
            segments.Add(new SnippetSegment(text, isMatch));
        }
    }
}
