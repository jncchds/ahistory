using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archive.Ai.Extraction;
using Archive.Ai.Llm;
using Archive.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ai.Merging;

/// <summary>A live fact, as the merge step sees it.</summary>
/// <param name="SubjectName">Who it is about, as the model is shown it — a name, or two for a pair.</param>
/// <param name="FirstSaidUnix">When the earliest message asserting it was sent; 0 when none is known.</param>
public sealed record MergeFact(
    string Id,
    string? PersonId,
    string? EdgeId,
    string SubjectName,
    string Predicate,
    string ObjectText,
    string ClaimText,
    string Source,
    double Confidence,
    int Citations,
    long FirstSaidUnix,
    string? ValidFromUtc)
{
    /// <summary>A correction the user made. Never folded into anything, never closed by a model.</summary>
    public bool IsUsers => Source == "user_edited";

    internal string SubjectKey => PersonId ?? "edge:" + EdgeId;
}

/// <summary>Two facts that might be one, numbered as the model is shown them.</summary>
public sealed record MergePair(int Number, MergeFact A, MergeFact B);

/// <summary>What merging would do for one person, before any of it is done.</summary>
/// <param name="Exact">
/// Pairs whose values are the same once case and punctuation are set aside. Folded without asking:
/// "Acme" and "acme." are not a judgement call, and a model asked about them costs a call to be
/// told so.
/// </param>
/// <param name="Ambiguous">Pairs a model has to look at, none of which it has judged before.</param>
public sealed record MergePlan(
    string PersonId,
    IReadOnlyList<(MergeFact Keep, MergeFact Fold)> Exact,
    IReadOnlyList<MergePair> Ambiguous)
{
    public bool IsEmpty => Exact.Count == 0 && Ambiguous.Count == 0;

    /// <summary>
    /// What the job is keyed on: exactly the work outstanding.
    /// </summary>
    /// <remarks>
    /// Judged pairs drop out of the plan, so a job that got through them leaves a different hash
    /// and the rest is queued; a job that could judge none of them leaves the same one, and is not
    /// asked again about pairs it has already failed on.
    /// </remarks>
    public string InputHash
    {
        get
        {
            var parts = Exact.Select(e => $"x:{e.Keep.Id}>{e.Fold.Id}")
                .Concat(Ambiguous.Select(p => $"p:{p.A.Id}|{p.B.Id}"))
                .Order(StringComparer.Ordinal);

            return FactMerger.Hash(
                PromptCatalog.VersionOf(PromptCatalog.AdjudicateFact) + "\n" + string.Join('\n', parts));
        }
    }
}

/// <summary>What one pass of merging did.</summary>
/// <param name="Folded">Facts folded into another, by exact match or by a "same" verdict.</param>
/// <param name="Judged">Pairs a model gave a verdict on.</param>
/// <param name="Changed">Facts closed because a newer value replaced them.</param>
/// <param name="NeedsReview">The model was asked and answered nothing usable.</param>
public sealed record MergeReport(int Folded, int Judged, int Changed, bool NeedsReview);

/// <summary>
/// Folds restatements of one fact into one, and closes values that were replaced (A4).
/// </summary>
/// <remarks>
/// <para>
/// The owner is profiled across every conversation, and the same fact surfaces in a dozen of them
/// phrased a dozen ways (spec §7). Without this the facts panel and the diary are an unreadable pile
/// of restatements.
/// </para>
/// <para>
/// Two stages, cheapest first. Candidates come from the normalized key the extraction prompt keeps
/// in lowercase English — same subject, same predicate — and values that match once case and
/// punctuation are set aside are folded with no model at all. Only what is left, and only pairs
/// that could plausibly be one thing, go to a model, which gives one of three answers: the same
/// thing, a value that changed, or two things that are both true.
/// </para>
/// <para>
/// The bias is deliberate and runs one way. An over-merge is a confident lie with twelve
/// citations; an under-merge is merely untidy. So "not sure" means different, a user's correction
/// is never folded or closed by a model, and every merge is a pointer that can be cleared rather
/// than a rewrite that cannot.
/// </para>
/// </remarks>
public sealed class FactMerger(Database database, AiClient? client = null, ILogger<FactMerger>? logger = null)
{
    /// <summary>How many pairs one call is asked about. More than this and a local model loses track.</summary>
    internal const int PairsPerCall = 20;

    /// <summary>
    /// A group this small has every pair looked at; a larger one only the pairs that look alike.
    /// </summary>
    /// <remarks>
    /// "lives_in: Berlin" and "lives_in: Prague" share no words and are exactly the pair that needs
    /// judging — so a handful of values are all compared. Thirty things someone likes are four
    /// hundred pairs, nearly all of them obviously different, and there only near-matches are asked about.
    /// </remarks>
    private const int SmallGroup = 6;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    private readonly ILogger _log = logger ?? NullLogger<FactMerger>.Instance;

    /// <summary>
    /// People with at least two live facts on the same attribute — the only ones with anything to merge.
    /// </summary>
    /// <remarks>
    /// Someone left out is not merged: the claims about them would go to the model, and "nothing they
    /// wrote is sent anywhere" has to cover what was read out of it too. A save that opted out is
    /// not merged at all.
    /// </remarks>
    public IReadOnlyList<string> PeopleWithCandidates()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT DISTINCT coalesce(f.subject_person_id, e.person_a_id) AS person
            FROM fact AS f
            LEFT JOIN person_edge AS e ON e.id = f.subject_edge_id
            JOIN person AS p ON p.id = coalesce(f.subject_person_id, e.person_a_id)
            WHERE f.superseded_by IS NULL
              AND f.merged_into IS NULL
              AND f.retracted_utc IS NULL
              AND f.source <> 'user_deleted'
              AND p.ai_excluded = 0
              AND NOT EXISTS (SELECT 1 FROM save_meta WHERE ai_opt_out = 1)
            GROUP BY coalesce(f.subject_person_id, 'edge:' || f.subject_edge_id), f.predicate
            HAVING count(*) >= 2;
            """;

        var people = new List<string>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            people.Add(reader.GetString(0));
        }

        return people;
    }

    /// <summary>What merging would do for a person now.</summary>
    public MergePlan Plan(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();

        var facts = LiveFacts(connection, personId);
        var judged = Judged(connection, facts.Select(f => f.Id));

        var exact = new List<(MergeFact, MergeFact)>();
        var ambiguous = new List<MergePair>();

        foreach (var group in facts.GroupBy(f => (f.SubjectKey, f.Predicate)).Where(g => g.Count() > 1))
        {
            // Values that are the same once normalized become one representative each.
            var representatives = new List<MergeFact>();

            foreach (var same in group.GroupBy(f => Key(f.ObjectText), StringComparer.Ordinal))
            {
                var ordered = same.OrderBy(f => f, Preference).ToList();
                var keep = ordered[0];

                representatives.Add(keep);

                // A user's own row is never folded into anything, even an identical one.
                exact.AddRange(ordered.Skip(1).Where(f => !f.IsUsers).Select(f => (keep, f)));
            }

            var compareAll = representatives.Count <= SmallGroup;

            for (var i = 0; i < representatives.Count; i++)
            {
                for (var j = i + 1; j < representatives.Count; j++)
                {
                    var (a, b) = Canonical(representatives[i], representatives[j]);

                    if ((a.IsUsers && b.IsUsers)
                        || judged.Contains((a.Id, b.Id))
                        || (!compareAll && !Similar(a.ObjectText, b.ObjectText)))
                    {
                        continue;
                    }

                    ambiguous.Add(new MergePair(ambiguous.Count + 1, a, b));
                }
            }
        }

        return new MergePlan(personId, exact, ambiguous);
    }

    /// <summary>
    /// Merges what can be merged for one person: the exact matches, then one call's worth of pairs.
    /// </summary>
    /// <remarks>
    /// One call, not all of them. Whatever is left is a different plan with a different hash, and
    /// the next pass queues it — so a person with two hundred candidate pairs is ten short jobs that
    /// each commit, rather than one long one that loses everything to a timeout on the tenth call.
    /// </remarks>
    public async Task<MergeReport> RunAsync(
        AiSettings settings, string personId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        ReleaseDangling();

        var plan = Plan(personId);
        var folded = FoldExact(plan.Exact);

        if (plan.Ambiguous.Count == 0)
        {
            return new MergeReport(folded, 0, 0, NeedsReview: false);
        }

        if (client is null)
        {
            throw new InvalidOperationException("Merging needs a model client to judge ambiguous pairs.");
        }

        var batch = plan.Ambiguous.Take(PairsPerCall).Select((p, i) => p with { Number = i + 1 }).ToList();
        var judge = new PairJudge(batch);
        var model = settings.ModelFor(AiWorkKind.Utility);

        var (calledAnything, _) = await ToolConversation.RunAsync(
            client,
            settings,
            model,
            PromptCatalog.TextOf(PromptCatalog.AdjudicateFact),
            Material(batch),
            MergeTools.All,
            judge.Dispatch,
            () => judge.AllJudged,
            AiPurpose.Adjudicate,
            AiSubject.Person(personId),
            cancellationToken).ConfigureAwait(false);

        if (!calledAnything || judge.Verdicts.Count == 0)
        {
            _log.LogWarning("Merging for {PersonId} produced no usable verdicts.", personId);

            return new MergeReport(folded, 0, 0, NeedsReview: true);
        }

        var (sameCount, changed) = Apply(judge.Verdicts, model);

        return new MergeReport(folded + sameCount, judge.Verdicts.Count, changed, NeedsReview: false);
    }

    /// <summary>
    /// The pairs as the model reads them: who, what, both values, and when each was first said.
    /// </summary>
    /// <remarks>
    /// The dates are the whole of "changed" — a value first said in 2019 and another first said in
    /// 2023 is the shape of a move or a new job — and the claims are there because a bare value is
    /// often ambiguous where the sentence it came from is not.
    /// </remarks>
    internal static string Material(IReadOnlyList<MergePair> pairs)
    {
        var text = new StringBuilder();

        foreach (var pair in pairs)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Pair {pair.Number} — {pair.A.SubjectName} · {pair.A.Predicate}");
            text.AppendLine(CultureInfo.InvariantCulture, $"  A: {Describe(pair.A)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"  B: {Describe(pair.B)}");
            text.AppendLine();
        }

        return text.ToString();

        static string Describe(MergeFact fact)
        {
            var when = fact.FirstSaidUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(fact.FirstSaidUnix).UtcDateTime
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "unknown";

            return $"{fact.ObjectText} — \"{fact.ClaimText}\" (first said {when}; "
                + $"{fact.Citations} message{(fact.Citations == 1 ? string.Empty : "s")})";
        }
    }

    private int FoldExact(IReadOnlyList<(MergeFact Keep, MergeFact Fold)> exact)
    {
        if (exact.Count == 0)
        {
            return 0;
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var folded = exact.Count(pair => Fold(connection, pair.Fold.Id, pair.Keep.Id));

        transaction.Commit();

        return folded;
    }

    /// <summary>Writes a call's verdicts, and what follows from them, in one short transaction.</summary>
    private (int Same, int Changed) Apply(IReadOnlyList<(MergePair Pair, MergeOutcome Outcome)> verdicts, string model)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var promptVersion = PromptCatalog.VersionOf(PromptCatalog.AdjudicateFact);

        var same = 0;
        var changed = 0;

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var (pair, outcome) in verdicts)
        {
            RecordVerdict(connection, pair, outcome, model, promptVersion, now);

            switch (outcome)
            {
                case MergeOutcome.Same:
                {
                    var (keep, fold) = Preference.Compare(pair.A, pair.B) <= 0 ? (pair.A, pair.B) : (pair.B, pair.A);

                    if (!fold.IsUsers && Fold(connection, fold.Id, keep.Id))
                    {
                        same++;
                    }

                    break;
                }

                case MergeOutcome.Changed:
                {
                    var (older, newer) = pair.A.FirstSaidUnix <= pair.B.FirstSaidUnix ? (pair.A, pair.B) : (pair.B, pair.A);

                    // The user's own row is not closed by a model, even as having expired.
                    if (!older.IsUsers && Expire(connection, older, newer))
                    {
                        changed++;
                    }

                    break;
                }
            }
        }

        transaction.Commit();

        return (same, changed);
    }

    /// <summary>
    /// Points one fact at the one it restates, and brings along anything already pointed at it.
    /// </summary>
    /// <remarks>
    /// Followed to the root first, so folding B into A after A was itself folded into C lands on C
    /// rather than building a chain the panel would have to walk.
    /// </remarks>
    /// <returns>False when the fact had already been folded, closed or removed.</returns>
    private static bool Fold(SqliteConnection connection, string foldId, string keepId)
    {
        var root = Root(connection, keepId);

        if (root == foldId)
        {
            return false;
        }

        using var fold = connection.CreateCommand();

        fold.CommandText = """
            UPDATE fact SET merged_into = $keep
            WHERE id = $fold AND merged_into IS NULL AND superseded_by IS NULL
              AND retracted_utc IS NULL AND source = 'extracted';
            """;

        fold.Parameters.AddWithValue("$keep", root);
        fold.Parameters.AddWithValue("$fold", foldId);

        if (fold.ExecuteNonQuery() == 0)
        {
            return false;
        }

        using var follow = connection.CreateCommand();

        follow.CommandText = "UPDATE fact SET merged_into = $keep WHERE merged_into = $fold;";
        follow.Parameters.AddWithValue("$keep", root);
        follow.Parameters.AddWithValue("$fold", foldId);
        follow.ExecuteNonQuery();

        return true;
    }

    private static string Root(SqliteConnection connection, string factId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT merged_into FROM fact WHERE id = $id;";

        var parameter = command.Parameters.AddWithValue("$id", factId);
        var current = factId;

        // Bounded: the pointers are written by this class and never form a cycle, but a loop that
        // trusted that and was wrong would hang a background job forever.
        for (var hops = 0; hops < 16; hops++)
        {
            parameter.Value = current;

            if (command.ExecuteScalar() is not string next)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// Closes a value that a newer one replaced — in event time, not assertion time.
    /// </summary>
    /// <remarks>
    /// "Works at Acme" from 2019 is not wrong; it expired. It stops on the date the new value is
    /// known to start from, or failing that the day the new value was first mentioned, so a diary
    /// narrating 2019 still finds it and the profile finds the new one (§7).
    /// </remarks>
    private static bool Expire(SqliteConnection connection, MergeFact older, MergeFact newer)
    {
        var validTo = newer.ValidFromUtc
            ?? (newer.FirstSaidUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(newer.FirstSaidUnix).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                : null);

        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE fact
            SET superseded_by = $newer, valid_to_utc = coalesce(valid_to_utc, $validTo)
            WHERE id = $older AND superseded_by IS NULL AND merged_into IS NULL AND retracted_utc IS NULL;
            """;

        command.Parameters.AddWithValue("$newer", Root(connection, newer.Id));
        command.Parameters.AddWithValue("$validTo", (object?)validTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$older", older.Id);

        return command.ExecuteNonQuery() > 0;
    }

    private static void RecordVerdict(
        SqliteConnection connection, MergePair pair, MergeOutcome outcome, string model, string promptVersion, string now)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO fact_pair_verdict (fact_a_id, fact_b_id, outcome, model, prompt_version, created_utc)
            VALUES ($a, $b, $outcome, $model, $prompt, $now)
            ON CONFLICT (fact_a_id, fact_b_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$a", pair.A.Id);
        command.Parameters.AddWithValue("$b", pair.B.Id);
        command.Parameters.AddWithValue("$outcome", outcome switch
        {
            MergeOutcome.Same => "same",
            MergeOutcome.Changed => "changed",
            _ => "different",
        });
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$prompt", promptVersion);
        command.Parameters.AddWithValue("$now", now);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Clears any merge pointer whose target no longer exists.
    /// </summary>
    /// <remarks>
    /// The column carries no foreign key (009_ai_complete.sql says why), so a fact that was folded
    /// into one since deleted would otherwise be invisible for good. Cleared, it is simply live
    /// again, and the next plan considers it afresh.
    /// </remarks>
    private void ReleaseDangling()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE fact SET merged_into = NULL
            WHERE merged_into IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM fact AS target WHERE target.id = fact.merged_into);
            """;

        command.ExecuteNonQuery();
    }

    private static List<MergeFact> LiveFacts(SqliteConnection connection, string personId)
    {
        using var command = connection.CreateCommand();

        // A person's own facts, and the facts on edges where they are the first of the pair — so an
        // edge is merged once, under one of its two people, rather than twice in parallel.
        command.CommandText = """
            SELECT f.id, f.subject_person_id, f.subject_edge_id,
                   coalesce(p.display_name, pa.display_name || ' & ' || pb.display_name, '?'),
                   f.predicate, f.object_text, f.claim_text, f.source, f.confidence,
                   (SELECT count(*) FROM fact_citation AS c WHERE c.fact_id = f.id),
                   coalesce((SELECT min(m.sent_at_unix)
                             FROM fact_citation AS c JOIN message AS m ON m.id = c.message_id
                             WHERE c.fact_id = f.id AND c.role = 'asserts'), 0),
                   f.valid_from_utc
            FROM fact AS f
            LEFT JOIN person AS p ON p.id = f.subject_person_id
            LEFT JOIN person_edge AS e ON e.id = f.subject_edge_id
            LEFT JOIN person AS pa ON pa.id = e.person_a_id
            LEFT JOIN person AS pb ON pb.id = e.person_b_id
            WHERE (f.subject_person_id = $person OR e.person_a_id = $person)
              AND f.superseded_by IS NULL
              AND f.merged_into IS NULL
              AND f.retracted_utc IS NULL
              AND f.source <> 'user_deleted'
            ORDER BY f.id;
            """;

        command.Parameters.AddWithValue("$person", personId);

        var facts = new List<MergeFact>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            facts.Add(new MergeFact(
                Id: reader.GetString(0),
                PersonId: reader.IsDBNull(1) ? null : reader.GetString(1),
                EdgeId: reader.IsDBNull(2) ? null : reader.GetString(2),
                SubjectName: reader.GetString(3),
                Predicate: reader.GetString(4),
                ObjectText: reader.GetString(5),
                ClaimText: reader.GetString(6),
                Source: reader.GetString(7),
                Confidence: reader.GetDouble(8),
                Citations: reader.GetInt32(9),
                FirstSaidUnix: reader.GetInt64(10),
                ValidFromUtc: reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return facts;
    }

    private static HashSet<(string, string)> Judged(SqliteConnection connection, IEnumerable<string> ids)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT fact_a_id, fact_b_id FROM fact_pair_verdict
            WHERE fact_a_id IN (SELECT value FROM json_each($ids));
            """;

        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.ToArray()));

        var judged = new HashSet<(string, string)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            judged.Add((reader.GetString(0), reader.GetString(1)));
        }

        return judged;
    }

    /// <summary>The pair in id order, which is how a verdict is stored.</summary>
    private static (MergeFact A, MergeFact B) Canonical(MergeFact x, MergeFact y) =>
        string.CompareOrdinal(x.Id, y.Id) < 0 ? (x, y) : (y, x);

    /// <summary>
    /// Which of two facts saying the same thing is kept.
    /// </summary>
    /// <remarks>
    /// The user's own first; then the one more messages stand behind; then the more confident; then
    /// the one heard first. The id last, only so the answer never depends on the order rows came
    /// back in.
    /// </remarks>
    private static readonly Comparer<MergeFact> Preference = Comparer<MergeFact>.Create((x, y) =>
    {
        var order = y.IsUsers.CompareTo(x.IsUsers);

        if (order == 0)
        {
            order = y.Citations.CompareTo(x.Citations);
        }

        if (order == 0)
        {
            order = y.Confidence.CompareTo(x.Confidence);
        }

        if (order == 0)
        {
            order = x.FirstSaidUnix.CompareTo(y.FirstSaidUnix);
        }

        return order != 0 ? order : string.CompareOrdinal(x.Id, y.Id);
    });

    /// <summary>
    /// A value with case, punctuation and articles set aside — equal keys are one value.
    /// </summary>
    internal static string Key(string value)
    {
        var letters = new StringBuilder(value.Length);

        foreach (var ch in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            letters.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return string.Join(' ', letters.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not ("the" or "a" or "an")));
    }

    /// <summary>
    /// Whether two values look enough alike to be worth asking about in a large group.
    /// </summary>
    /// <remarks>
    /// One containing the other ("acme" in "acme corp"), or sharing half their words. Cheap, and
    /// wrong in both directions sometimes — which is fine, because it only decides what is asked,
    /// never what is merged.
    /// </remarks>
    internal static bool Similar(string a, string b)
    {
        var x = Key(a);
        var y = Key(b);

        if (x.Length >= 3 && y.Length >= 3 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)))
        {
            return true;
        }

        var left = x.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var right = y.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        var shared = left.Count(right.Contains);

        return shared * 2 >= left.Union(right, StringComparer.Ordinal).Count();
    }

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>The three answers a pair can get.</summary>
public enum MergeOutcome
{
    Same,
    Changed,
    Different,
}

/// <summary>The one tool merging uses. Flat, like every schema here (ai-plan.md §5.0b).</summary>
internal static class MergeTools
{
    public const string JudgePair = "judge_pair";

    public static IReadOnlyList<LlmToolDefinition> All { get; } =
    [
        new(
            JudgePair,
            "Give your verdict on one numbered pair: same, changed, or different. Call it once per pair.",
            """
            {
              "type": "object",
              "properties": {
                "pair": { "type": "integer", "description": "The pair number." },
                "outcome": { "type": "string", "description": "same, changed or different." },
                "reason": { "type": "string", "description": "One short phrase." }
              },
              "required": ["pair", "outcome"]
            }
            """),
    ];
}

/// <summary>Takes verdicts, refuses the ones that do not make sense, and stages the rest.</summary>
internal sealed class PairJudge(IReadOnlyList<MergePair> pairs)
{
    private readonly Dictionary<int, MergePair> _pairs = pairs.ToDictionary(p => p.Number);

    public List<(MergePair Pair, MergeOutcome Outcome)> Verdicts { get; } = [];

    public bool AllJudged => Verdicts.Count == _pairs.Count;

    public ToolReply Dispatch(string name, string argumentsJson)
    {
        if (name != MergeTools.JudgePair)
        {
            return new ToolReply(false, $"There is no tool called {name}.");
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var arguments = document.RootElement;

            if (!arguments.TryGetProperty("pair", out var number)
                || !number.TryGetInt32(out var n)
                || !_pairs.TryGetValue(n, out var pair))
            {
                return new ToolReply(false, "pair must be one of the numbers shown.");
            }

            if (Verdicts.Any(v => v.Pair.Number == n))
            {
                return new ToolReply(false, $"Pair {n} has already been judged.");
            }

            var outcome = arguments.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim().ToLowerInvariant()
                : null;

            MergeOutcome? parsed = outcome switch
            {
                "same" => MergeOutcome.Same,
                "changed" => MergeOutcome.Changed,
                "different" => MergeOutcome.Different,
                _ => null,
            };

            if (parsed is null)
            {
                return new ToolReply(false, "outcome must be same, changed or different.");
            }

            Verdicts.Add((pair, parsed.Value));

            return new ToolReply(true, "Noted.");
        }
        catch (JsonException)
        {
            return new ToolReply(false, "Those arguments were not valid JSON. Send the call again.");
        }
    }
}
