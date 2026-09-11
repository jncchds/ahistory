using System.Globalization;
using Archive.Ai.Extraction;
using Archive.Ai.Sessions;
using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Ai.Diary;

/// <summary>How much someone was in touch in one calendar month.</summary>
public sealed record MonthActivity(int Year, int Month, long Messages, int Days)
{
    public DateTime Start => new(Year, Month, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime End => Start.AddMonths(1);

    public long StartUnix => new DateTimeOffset(Start).ToUnixTimeSeconds();

    public long EndUnix => new DateTimeOffset(End).ToUnixTimeSeconds();

    /// <summary>"2019-03" — how a month is named in a job's subject and in a hash.</summary>
    public string Key => Start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}

/// <summary>The person a diary is about.</summary>
public sealed record DiaryPerson(string Id, string Name, bool IsOwner);

/// <summary>A fact, and the message in the window that says it.</summary>
public sealed record DiaryFact(string Id, string Claim, long MessageId);

/// <summary>Everything one month's entry is written from.</summary>
/// <param name="QuietMonthsBefore">
/// Months without a single message before this one; null when this is the first. Given to the model
/// rather than left for it to notice, because a summariser asked to notice absence papers over it
/// (ai-plan.md §5.3).
/// </param>
public sealed record MonthInput(
    DiaryPerson Person,
    MonthActivity Month,
    int? QuietMonthsBefore,
    IReadOnlyList<DiaryFact> Facts,
    IReadOnlyList<WindowMessage> Messages)
{
    public HashSet<long> CitableIds { get; } = [.. Facts.Select(f => f.MessageId), .. Messages.Select(m => m.Id)];
}

/// <summary>Everything a year summary or a profile is written from: the texts below it, and facts.</summary>
public sealed record SummaryInput(
    DiaryPerson Person,
    int? Year,
    IReadOnlyList<(string Label, IReadOnlyList<DiarySentence> Sentences)> Parts,
    IReadOnlyList<DiaryFact> Facts,
    int ActiveMonths)
{
    public HashSet<long> CitableIds { get; } =
        [.. Parts.SelectMany(p => p.Sentences).SelectMany(s => s.MessageIds), .. Facts.Select(f => f.MessageId)];

    /// <summary>What the job is keyed on: the texts and facts below, not the time it was asked.</summary>
    public string InputHash(string promptVersion) =>
        FactMergerHash(promptVersion + "\n"
            + string.Join('\n', Parts.SelectMany(p => p.Sentences).Select(s => s.Text + "|" + string.Join(',', s.MessageIds)))
            + "\n" + string.Join('\n', Facts.Select(f => f.Id + "|" + f.Claim)));

    private static string FactMergerHash(string value) => Merging.FactMerger.Hash(value);
}

/// <summary>
/// Gathers what a diary text is written from — and, just as much, what it must not be written from.
/// </summary>
/// <remarks>
/// <para>
/// Spec §6.4: each level summarises the level below and never raw messages. A month is the lowest
/// level with prose, so its "level below" is the facts read out of that month's sessions — plus, for
/// a contact, excerpts of the direct conversations those facts came from, because a fact is a key
/// and a value and a diary entry needs to know what the month felt like. A year is written from its
/// months, and the profile from its years. Nothing above a month reads a message.
/// </para>
/// <para>
/// The owner gets facts only. Their month is every conversation they had that month, which does not
/// fit in a prompt and would send every one of them; the facts about them are what the spec means
/// by profiling the owner across all threads.
/// </para>
/// <para>
/// The same exclusions as extraction, applied here again rather than trusted from upstream: a person
/// left out has no diary, an excluded conversation contributes no excerpt, and a line by someone
/// left out is not quoted.
/// </para>
/// </remarks>
public sealed class DiaryInputs(Database database)
{
    /// <summary>How much conversation a month's entry is shown, at most, across all its sessions.</summary>
    private const int ExcerptBudget = 9_000;

    /// <summary>The least any one session is given, so a busy month still shows each conversation.</summary>
    private const int ExcerptFloor = 600;

    /// <summary>How many facts a month entry or a profile is shown.</summary>
    private const int FactLimit = 40;

    /// <summary>
    /// The facts a diary may speak from.
    /// </summary>
    /// <remarks>
    /// Superseded facts are included — they were true then, and the diary for 2019 needs the job
    /// someone had in 2019 (§7) — except where the user corrected them, when the correction speaks
    /// instead. Merged duplicates speak through the fact they were folded into.
    /// </remarks>
    internal const string VisibleFact = """
        f.merged_into IS NULL
        AND f.retracted_utc IS NULL
        AND f.source <> 'user_deleted'
        AND NOT EXISTS (SELECT 1 FROM fact AS s WHERE s.id = f.superseded_by AND s.source = 'user_edited')
        """;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>The person, or null when they are not to be written about.</summary>
    public DiaryPerson? Person(string personId)
    {
        using var connection = _database.Open();

        return Person(connection, personId);
    }

    /// <summary>
    /// Every month someone was in touch: their direct conversations, and what they said anywhere.
    /// </summary>
    public IReadOnlyList<MonthActivity> Months(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            WITH mine AS (SELECT identity_id FROM identity_person WHERE person_id = $person),
                 direct AS (
                     SELECT tp.thread_id
                     FROM thread_participant AS tp
                     JOIN thread AS t ON t.id = tp.thread_id
                     WHERE t.kind = 'dm' AND tp.identity_id IN (SELECT identity_id FROM mine))
            SELECT strftime('%Y', m.sent_at_unix, 'unixepoch'),
                   strftime('%m', m.sent_at_unix, 'unixepoch'),
                   count(*),
                   count(DISTINCT m.sent_at_unix / 86400)
            FROM message AS m
            WHERE m.kind = 'message' AND m.is_deleted = 0
              AND (m.thread_id IN (SELECT thread_id FROM direct)
                   OR m.sender_identity_id IN (SELECT identity_id FROM mine))
            GROUP BY 1, 2
            ORDER BY 1, 2;
            """;

        command.Parameters.AddWithValue("$person", personId);

        var months = new List<MonthActivity>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            months.Add(new MonthActivity(
                int.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                int.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetInt64(2),
                reader.GetInt32(3)));
        }

        return months;
    }

    /// <summary>What one month's entry is written from, or null when there is nothing to write from.</summary>
    public MonthInput? Month(string personId, int year, int month)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();

        var person = Person(connection, personId);

        if (person is null)
        {
            return null;
        }

        var months = Months(personId);
        var index = months.ToList().FindIndex(m => m.Year == year && m.Month == month);

        var window = index >= 0 ? months[index] : new MonthActivity(year, month, 0, 0);

        int? quiet = index > 0
            ? MonthsBetween(months[index - 1].Start, window.Start) - 1
            : null;

        var facts = FactsIn(connection, personId, window.StartUnix, window.EndUnix);

        if (facts.Count == 0)
        {
            return null;
        }

        var messages = person.IsOwner
            ? []
            : Excerpts(connection, personId, window.StartUnix, window.EndUnix);

        return new MonthInput(person, window, quiet, facts, messages);
    }

    /// <summary>What a year summary is written from: that year's month entries.</summary>
    public SummaryInput? Year(string personId, int year, DiaryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var person = Person(personId);

        if (person is null)
        {
            return null;
        }

        var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var end = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var parts = store.Latest(personId, DiaryScope.Month)
            .Select(v => v.Latest)
            .Where(e => e.StartUnix >= start && e.StartUnix < end && e.Sentences.Count > 0)
            .Select(e => (Label(e.StartUnix!.Value, "MMMM"), e.Sentences))
            .ToList();

        var active = Months(personId).Count(m => m.Year == year);

        return new SummaryInput(person, year, parts, [], active);
    }

    /// <summary>What a profile is written from: the years, the latest months, and what is known.</summary>
    public SummaryInput? Profile(string personId, DiaryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        using var connection = _database.Open();

        var person = Person(connection, personId);

        if (person is null)
        {
            return null;
        }

        var years = store.Latest(personId, DiaryScope.Year)
            .Select(v => v.Latest)
            .Where(e => e.Sentences.Count > 0)
            .Select(e => (Label(e.StartUnix!.Value, "yyyy"), e.Sentences))
            .ToList();

        // The most recent months too, which no year summary covers yet — the profile has to know
        // how things stand now, not as of last December.
        var recent = store.Latest(personId, DiaryScope.Month)
            .Select(v => v.Latest)
            .Where(e => e.Sentences.Count > 0)
            .TakeLast(6)
            .Select(e => (Label(e.StartUnix!.Value, "MMMM yyyy"), e.Sentences));

        var facts = FactsIn(connection, personId, long.MinValue, long.MaxValue);

        return new SummaryInput(person, null, [.. years, .. recent], facts, Months(personId).Count);
    }

    private static string Label(long unix, string format) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString(format, CultureInfo.InvariantCulture);

    private static int MonthsBetween(DateTime earlier, DateTime later) =>
        ((later.Year - earlier.Year) * 12) + later.Month - earlier.Month;

    private static DiaryPerson? Person(SqliteConnection connection, string personId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT p.id, p.display_name, p.is_owner
            FROM person AS p
            WHERE p.id = $person
              AND p.ai_excluded = 0
              AND NOT EXISTS (SELECT 1 FROM save_meta WHERE ai_opt_out = 1);
            """;

        command.Parameters.AddWithValue("$person", personId);

        using var reader = command.ExecuteReader();

        return reader.Read()
            ? new DiaryPerson(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1)
            : null;
    }

    /// <summary>
    /// The facts about someone that a window's messages said, with the first message in it that did.
    /// </summary>
    /// <remarks>
    /// A fact belongs to the months its evidence was said in — its own citations and those of
    /// everything folded into it — which is what lets a merged fact appear in every month someone
    /// mentioned the new job, citing that month's message each time.
    /// </remarks>
    private static List<DiaryFact> FactsIn(SqliteConnection connection, string personId, long start, long end)
    {
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT f.id, f.claim_text, min(m.id)
            FROM fact AS f
            LEFT JOIN person_edge AS e ON e.id = f.subject_edge_id
            JOIN fact AS src ON src.id = f.id OR src.merged_into = f.id
            JOIN fact_citation AS c ON c.fact_id = src.id AND c.role IN ('asserts', 'corroborates')
            JOIN message AS m ON m.id = c.message_id
            WHERE (f.subject_person_id = $person OR e.person_a_id = $person OR e.person_b_id = $person)
              AND {VisibleFact}
              AND m.sent_at_unix >= $start AND m.sent_at_unix < $end
            GROUP BY f.id
            ORDER BY min(m.sent_at_unix), f.id
            LIMIT {FactLimit};
            """;

        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);

        var facts = new List<DiaryFact>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            facts.Add(new DiaryFact(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return facts;
    }

    /// <summary>
    /// The month's direct conversations with someone, cut to fit.
    /// </summary>
    /// <remarks>
    /// Only the sessions the filter thought worth reading, and each given an even share of the
    /// budget from its start — so a month with thirty conversations shows the opening of each rather
    /// than the whole of the first three.
    /// </remarks>
    private static List<WindowMessage> Excerpts(SqliteConnection connection, string personId, long start, long end)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT m.session_id, m.id, ip.person_id, coalesce(p.display_name, i.display_name, '?'),
                   m.sent_at_unix, m.plaintext
            FROM session AS s
            JOIN thread AS t ON t.id = s.thread_id
            JOIN message AS m ON m.session_id = s.id
            LEFT JOIN identity AS i ON i.id = m.sender_identity_id
            LEFT JOIN identity_person AS ip ON ip.identity_id = i.id
            LEFT JOIN person AS p ON p.id = ip.person_id
            WHERE s.is_substantive = 1
              AND s.segmenter_version = $segmenter
              AND t.kind = 'dm'
              AND t.ai_excluded = 0
              AND s.thread_id IN (
                  SELECT tp.thread_id
                  FROM thread_participant AS tp
                  JOIN identity_person AS mine ON mine.identity_id = tp.identity_id
                  WHERE mine.person_id = $person)
              AND s.started_at_unix >= $start AND s.started_at_unix < $end
              AND m.kind = 'message' AND m.is_deleted = 0 AND m.plaintext <> ''
              AND (p.id IS NULL OR p.ai_excluded = 0)
            ORDER BY m.sent_at_unix, m.id;
            """;

        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$segmenter", SessionSegmenter.Version);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);

        var bySession = new List<(string Session, List<WindowMessage> Messages)>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var session = reader.GetString(0);

                if (bySession.Count == 0 || bySession[^1].Session != session)
                {
                    bySession.Add((session, []));
                }

                bySession[^1].Messages.Add(new WindowMessage(
                    Id: reader.GetInt64(1),
                    SenderPersonId: reader.IsDBNull(2) ? null : reader.GetString(2),
                    SenderName: reader.GetString(3),
                    SentAtUnix: reader.GetInt64(4),
                    Text: reader.GetString(5)));
            }
        }

        if (bySession.Count == 0)
        {
            return [];
        }

        var share = Math.Max(ExcerptFloor, ExcerptBudget / bySession.Count);
        var excerpt = new List<WindowMessage>();
        var total = 0;

        foreach (var (_, messages) in bySession)
        {
            var used = 0;

            foreach (var message in messages)
            {
                if (used + message.Text.Length > share || total + message.Text.Length > ExcerptBudget)
                {
                    break;
                }

                excerpt.Add(message);
                used += message.Text.Length;
                total += message.Text.Length;
            }
        }

        return excerpt;
    }
}
