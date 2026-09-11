using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Sessions;
using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Ai.Diary;

/// <summary>A diary text to be written, as the queue needs to know it.</summary>
public sealed record DiaryWork(AiJobKind Kind, string SubjectId, string InputHash, int Priority);

/// <summary>
/// Decides which diary texts are out of date, and which are ready to be written.
/// </summary>
/// <remarks>
/// <para>
/// Spec §8's cache rule: a month is keyed by what it is written from, so an import that touches
/// March 2019 regenerates March 2019 and nothing else. Here that key is the facts the month can
/// speak from and the membership of its conversations — never the time it was asked — so asking
/// again when nothing changed finds the job it already has.
/// </para>
/// <para>
/// And ai-plan.md §11.1's debounce: a month waits until everything beneath it is settled — no
/// conversation in it still waiting to be read, and no merge for the person still to run. Without
/// that, a first pass over an archive would rewrite the same entry every time one more session in
/// the month landed, on the expensive model. A year waits for its months, and the profile for
/// everything.
/// </para>
/// </remarks>
public sealed class DiaryPlanner(Database database, DiaryInputs inputs, DiaryStore store)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    private readonly DiaryInputs _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));

    private readonly DiaryStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>The months that are out of date and settled enough to write.</summary>
    public IReadOnlyList<DiaryWork> Months()
    {
        var prompt = PromptCatalog.VersionOf(PromptCatalog.DiaryWindow);
        var work = new List<DiaryWork>();

        using var connection = _database.Open();

        foreach (var (personId, isOwner) in People(connection))
        {
            if (Busy(connection, AiJobKind.Adjudicate, personId, exact: true))
            {
                continue;
            }

            var unsettled = UnreadMonths(connection, personId);
            var conversations = isOwner ? [] : ConversationHashes(connection, personId);

            foreach (var (month, facts) in FactsByMonth(connection, personId))
            {
                if (unsettled.Contains(month))
                {
                    continue;
                }

                var members = conversations.TryGetValue(month, out var hashes) ? hashes : [];

                var hash = Merging.FactMerger.Hash(
                    prompt + "\n" + string.Join('\n', facts) + "\n" + string.Join('\n', members));

                var end = DateTime.ParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
                    .AddMonths(1);

                work.Add(new DiaryWork(
                    AiJobKind.Diary,
                    $"{personId}|{month}",
                    hash,
                    (int)(new DateTimeOffset(end).ToUnixTimeSeconds() / 86_400)));
            }
        }

        return work;
    }

    /// <summary>
    /// The year summaries and profiles that are out of date, for people whose months are all written.
    /// </summary>
    /// <remarks>
    /// Asked after the months are queued, so a month queued a moment ago counts as unwritten and
    /// holds its year back.
    /// </remarks>
    public IReadOnlyList<DiaryWork> Summaries()
    {
        var yearPrompt = PromptCatalog.VersionOf(PromptCatalog.RollupYear);
        var profilePrompt = PromptCatalog.VersionOf(PromptCatalog.RollupProfile);
        var work = new List<DiaryWork>();

        List<(string PersonId, bool IsOwner)> people;

        using (var connection = _database.Open())
        {
            people = [.. People(connection).Where(p => !Busy(connection, AiJobKind.Diary, p.PersonId, exact: false)
                                                       && !Busy(connection, AiJobKind.Adjudicate, p.PersonId, exact: true))];
        }

        foreach (var (personId, _) in people)
        {
            var months = _store.Latest(personId, DiaryScope.Month)
                .Select(v => v.Latest)
                .Where(e => e.Sentences.Count > 0)
                .ToList();

            var years = months
                .GroupBy(e => DateTimeOffset.FromUnixTimeSeconds(e.StartUnix!.Value).Year)
                .Where(g => g.Count() >= 2)
                .Select(g => g.Key)
                .ToList();

            var yearsBusy = false;

            foreach (var year in years)
            {
                var input = _inputs.Year(personId, year, _store);

                if (input is null)
                {
                    continue;
                }

                var hash = input.InputHash(yearPrompt);

                work.Add(new DiaryWork(AiJobKind.Rollup, $"{personId}|{year}", hash, 0));
                yearsBusy |= !YearIsWritten(personId, year, hash);
            }

            // The profile waits for its years, so it is written once from the finished set rather
            // than once per year as they arrive.
            if (yearsBusy || months.Count == 0)
            {
                continue;
            }

            var profile = _inputs.Profile(personId, _store);

            if (profile is { Facts.Count: >= 3 })
            {
                work.Add(new DiaryWork(AiJobKind.Rollup, personId, profile.InputHash(profilePrompt), 0));
            }
        }

        return work;
    }

    /// <summary>Whether this year's summary already exists from exactly these months.</summary>
    private bool YearIsWritten(string personId, int year, string inputHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM derived_artifact
                WHERE source_person_id = $person AND kind = 'rollup'
                  AND window_start_unix = $start AND input_hash = $hash);
            """;

        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$start", new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$hash", inputHash);

        return command.ExecuteScalar() is long found && found == 1;
    }

    /// <summary>People who have facts and may be written about.</summary>
    private static List<(string PersonId, bool IsOwner)> People(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT p.id, p.is_owner
            FROM person AS p
            WHERE p.ai_excluded = 0
              AND NOT EXISTS (SELECT 1 FROM save_meta WHERE ai_opt_out = 1)
              AND EXISTS (
                  SELECT 1 FROM fact AS f
                  LEFT JOIN person_edge AS e ON e.id = f.subject_edge_id
                  WHERE (f.subject_person_id = p.id OR e.person_a_id = p.id OR e.person_b_id = p.id)
                    AND f.retracted_utc IS NULL AND f.source <> 'user_deleted')
            ORDER BY p.id;
            """;

        var people = new List<(string, bool)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            people.Add((reader.GetString(0), reader.GetInt64(1) == 1));
        }

        return people;
    }

    /// <summary>
    /// Whether work of a kind is still waiting for this person — a merge (subject is the person) or
    /// any of their diary months (subject starts with the person).
    /// </summary>
    private static bool Busy(SqliteConnection connection, AiJobKind kind, string personId, bool exact)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM ai_job
                WHERE kind = $kind AND state IN ('pending', 'running')
                  AND (subject_id = $person
                       OR ($prefix = 1 AND substr(subject_id, 1, length($person) + 1) = $person || '|')));
            """;

        command.Parameters.AddWithValue("$kind", kind == AiJobKind.Adjudicate ? "adjudicate" : "diary");
        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$prefix", exact ? 0 : 1);

        return command.ExecuteScalar() is long busy && busy == 1;
    }

    /// <summary>Months with a conversation of this person's still waiting to be read.</summary>
    private static HashSet<string> UnreadMonths(SqliteConnection connection, string personId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT DISTINCT strftime('%Y-%m', s.started_at_unix, 'unixepoch')
            FROM ai_job AS j
            JOIN session AS s ON s.id = j.subject_id
            WHERE j.kind = 'extract' AND j.state IN ('pending', 'running')
              AND s.thread_id IN (
                  SELECT tp.thread_id
                  FROM thread_participant AS tp
                  JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
                  WHERE ip.person_id = $person);
            """;

        command.Parameters.AddWithValue("$person", personId);

        var months = new HashSet<string>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            months.Add(reader.GetString(0));
        }

        return months;
    }

    /// <summary>Each month's facts, as "id|claim" lines — the part of a month's key facts make.</summary>
    private static List<(string Month, List<string> Facts)> FactsByMonth(SqliteConnection connection, string personId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT strftime('%Y-%m', m.sent_at_unix, 'unixepoch') AS month, f.id, f.claim_text
            FROM fact AS f
            LEFT JOIN person_edge AS e ON e.id = f.subject_edge_id
            JOIN fact AS src ON src.id = f.id OR src.merged_into = f.id
            JOIN fact_citation AS c ON c.fact_id = src.id AND c.role IN ('asserts', 'corroborates')
            JOIN message AS m ON m.id = c.message_id
            WHERE (f.subject_person_id = $person OR e.person_a_id = $person OR e.person_b_id = $person)
              AND {DiaryInputs.VisibleFact}
            GROUP BY month, f.id
            ORDER BY month, f.id;
            """;

        command.Parameters.AddWithValue("$person", personId);

        var months = new List<(string, List<string>)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var month = reader.GetString(0);

            if (months.Count == 0 || months[^1].Item1 != month)
            {
                months.Add((month, []));
            }

            months[^1].Item2.Add(reader.GetString(1) + "|" + reader.GetString(2));
        }

        return months;
    }

    /// <summary>The membership of each month's direct conversations — the part of the key messages make.</summary>
    private static Dictionary<string, List<string>> ConversationHashes(SqliteConnection connection, string personId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT strftime('%Y-%m', s.started_at_unix, 'unixepoch'), s.member_hash
            FROM session AS s
            JOIN thread AS t ON t.id = s.thread_id
            WHERE s.is_substantive = 1 AND s.segmenter_version = $segmenter
              AND t.kind = 'dm' AND t.ai_excluded = 0
              AND s.thread_id IN (
                  SELECT tp.thread_id
                  FROM thread_participant AS tp
                  JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
                  WHERE ip.person_id = $person)
            ORDER BY 1, 2;
            """;

        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$segmenter", SessionSegmenter.Version);

        var months = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var month = reader.GetString(0);

            if (!months.TryGetValue(month, out var list))
            {
                months[month] = list = [];
            }

            list.Add(reader.GetString(1));
        }

        return months;
    }
}
