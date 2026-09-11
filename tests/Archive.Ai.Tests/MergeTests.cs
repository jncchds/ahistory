using System.Text.Json;
using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Merging;
using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// One fact said many ways becomes one fact with many citations — and nothing is merged that
/// should not be (A4).
/// </summary>
public sealed class MergeTests : IDisposable
{
    private const long Noon = 1_700_000_000;
    private const long Year = 365 * 86_400;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private static AiSettings Settings() => new()
    {
        Enabled = true,
        Endpoint = "http://localhost:1234/v1",
        MainModel = "test-model",
        MaxRetries = 0,
        RetryBaseDelayMs = 1,
        DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
    };

    /// <summary>A thread with messages a year apart, so "first said" can tell old from new.</summary>
    private static (TempSave Save, long First, long Second, long Third) Seeded()
    {
        var save = new TempSave();

        save.Seed(
            "t1",
            (Noon, "I work at Acme"),
            (Noon + Year, "I work at Acme Corp, same place"),
            (Noon + (2 * Year), "moved to Globex this spring"));

        var first = save.Count("SELECT min(id) FROM message;");

        return (save, first, first + 1, first + 2);
    }

    /// <summary>Puts a fact about p_them straight into the store, cited to one message.</summary>
    private static void Fact(
        TempSave save, string id, string predicate, string value, long messageId, string source = "extracted")
    {
        save.Execute($$"""
            INSERT OR IGNORE INTO derived_artifact (id, kind, engine, model, model_version, payload_json,
                                                    input_hash, created_utc)
                VALUES ('a1', 'session_extract', 'llm', 'm', 'unknown', '{}', 'h', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO fact (id, subject_person_id, predicate, object_text, claim_text, evidence_kind,
                              origin_kind, confidence, asserted_utc, derived_artifact_id, source)
                VALUES ('{{id}}', 'p_them', '{{predicate}}', '{{value}}', 'Them: {{value}}.', 'self_report',
                        'dm', 0.8, '2026-01-01T00:00:00.0000000Z', 'a1', '{{source}}');
            INSERT INTO fact_citation (fact_id, message_id, role) VALUES ('{{id}}', {{messageId}}, 'asserts');
            """);
    }

    /// <summary>A completion that judges pairs, by number.</summary>
    private static string Verdicts(params (int Pair, string Outcome)[] verdicts)
    {
        var calls = verdicts.Select((v, i) => new
        {
            id = $"c{i}",
            type = "function",
            function = new
            {
                name = MergeTools.JudgePair,
                arguments = JsonSerializer.Serialize(new { pair = v.Pair, outcome = v.Outcome }),
            },
        });

        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = (string?)null, tool_calls = calls }, finish_reason = "tool_calls" },
            },
        });
    }

    private static FactMerger Merger(TempSave save, FakeEndpoint endpoint) =>
        new(save.Database, new AiClient(new LlmProviderFactory(endpoint), save.Interactions));

    /// <summary>
    /// "Acme" and "acme." are one value, and a model asked about them costs a call to be told so.
    /// </summary>
    [Fact]
    public async Task Values_that_differ_only_in_case_and_punctuation_are_folded_without_a_model()
    {
        var (save, first, second, _) = Seeded();

        using (save)
        {
            Fact(save, "f1", "works_at", "Acme", first);
            Fact(save, "f2", "works_at", "acme.", second);

            var endpoint = new FakeEndpoint();
            var report = await Merger(save, endpoint).RunAsync(Settings(), "p_them");

            Assert.Equal(1, report.Folded);
            Assert.Empty(endpoint.Requests);

            // One fact in the panel, carrying the evidence of both.
            var shown = Assert.Single(new FactStore(save.Database).ForPerson("p_them"));

            Assert.Equal(2, shown.CitationCount);
        }
    }

    [Fact]
    public async Task A_pair_judged_the_same_becomes_one_fact_with_both_citations()
    {
        var (save, first, second, _) = Seeded();

        using (save)
        {
            Fact(save, "f1", "works_at", "Acme", first);
            Fact(save, "f2", "works_at", "Acme Corp", second);

            var endpoint = new FakeEndpoint().Answers(Verdicts((1, "same")));
            var report = await Merger(save, endpoint).RunAsync(Settings(), "p_them");

            Assert.Equal(1, report.Folded);
            Assert.Equal(2, Assert.Single(new FactStore(save.Database).ForPerson("p_them")).CitationCount);

            // The pair went to the model with its dates, which is what "changed" is judged on.
            Assert.Contains("first said", endpoint.Requests[0].Body, StringComparison.Ordinal);
            Assert.Equal("same", save.Text("SELECT outcome FROM fact_pair_verdict;"));
        }
    }

    /// <summary>"Works at Acme" from 2019 is not wrong; it expired (§7).</summary>
    [Fact]
    public async Task A_value_judged_changed_closes_the_older_one_in_event_time()
    {
        var (save, first, _, third) = Seeded();

        using (save)
        {
            Fact(save, "f1", "works_at", "Acme", first);
            Fact(save, "f2", "works_at", "Globex", third);

            var endpoint = new FakeEndpoint().Answers(Verdicts((1, "changed")));
            var report = await Merger(save, endpoint).RunAsync(Settings(), "p_them");

            Assert.Equal(1, report.Changed);
            Assert.Equal("f2", save.Text("SELECT superseded_by FROM fact WHERE id = 'f1';"));
            Assert.NotNull(save.Text("SELECT valid_to_utc FROM fact WHERE id = 'f1';"));

            // Closed, not retracted: it was believed, and was true then.
            Assert.Null(save.Text("SELECT retracted_utc FROM fact WHERE id = 'f1';"));
            Assert.Equal("Globex", Assert.Single(new FactStore(save.Database).ForPerson("p_them")).ObjectText);
        }
    }

    /// <summary>
    /// "Different" changes nothing, and is remembered — otherwise every pass would ask again.
    /// </summary>
    [Fact]
    public async Task A_pair_judged_different_is_left_alone_and_not_asked_about_again()
    {
        var (save, first, second, _) = Seeded();

        using (save)
        {
            Fact(save, "f1", "has_child", "Anna", first);
            Fact(save, "f2", "has_child", "Mark", second);

            var endpoint = new FakeEndpoint().Answers(Verdicts((1, "different")));
            var merger = Merger(save, endpoint);

            await merger.RunAsync(Settings(), "p_them");

            Assert.Equal(2, new FactStore(save.Database).ForPerson("p_them").Count);
            Assert.True(merger.Plan("p_them").IsEmpty);
            Assert.DoesNotContain(merger.PeopleWithCandidates(), p => !merger.Plan(p).IsEmpty);
        }
    }

    /// <summary>The user outranks the model: their row is what is kept, and never what is closed.</summary>
    [Fact]
    public async Task A_users_correction_is_kept_and_never_closed_by_a_model()
    {
        var (save, first, second, third) = Seeded();

        using (save)
        {
            Fact(save, "f1", "works_at", "Acme", first, source: "user_edited");
            Fact(save, "f2", "works_at", "ACME", second);
            Fact(save, "f3", "works_at", "Globex", third);

            // f2 folds into the user's row without asking; the user's row against Globex is judged
            // "changed" — and the user's row is still not closed, because a model does not get to.
            var endpoint = new FakeEndpoint().Answers(Verdicts((1, "changed")));
            await Merger(save, endpoint).RunAsync(Settings(), "p_them");

            Assert.Equal("f1", save.Text("SELECT merged_into FROM fact WHERE id = 'f2';"));
            Assert.Null(save.Text("SELECT superseded_by FROM fact WHERE id = 'f1';"));
            Assert.Null(save.Text("SELECT merged_into FROM fact WHERE id = 'f1';"));
        }
    }

    /// <summary>
    /// A merge is only as good as what it merged into: retract that, and the duplicate comes back.
    /// </summary>
    [Fact]
    public async Task Retracting_a_fact_releases_what_was_folded_into_it()
    {
        var (save, first, second, _) = Seeded();

        using (save)
        {
            Fact(save, "f1", "works_at", "Acme", first);
            Fact(save, "f2", "works_at", "acme", second);

            await Merger(save, new FakeEndpoint()).RunAsync(Settings(), "p_them");

            var kept = save.Text("SELECT id FROM fact WHERE merged_into IS NULL;")!;
            save.Execute($"UPDATE fact SET retracted_utc = '2026-02-01T00:00:00.0000000Z' WHERE id = '{kept}';");

            using (var connection = save.Database.Open())
            {
                FactWriter.ReleaseFromRetracted(connection);
            }

            Assert.Equal(0, save.Count("SELECT count(*) FROM fact WHERE merged_into IS NOT NULL;"));
            Assert.Single(new FactStore(save.Database).ForPerson("p_them"));
        }
    }

    /// <summary>Removing a fact removes its restatements too, or it would not stay removed.</summary>
    [Fact]
    public async Task Removing_a_fact_removes_what_was_folded_into_it()
    {
        var (save, first, second, _) = Seeded();

        using (save)
        {
            Fact(save, "f1", "works_at", "Acme", first);
            Fact(save, "f2", "works_at", "acme", second);

            await Merger(save, new FakeEndpoint()).RunAsync(Settings(), "p_them");

            var store = new FactStore(save.Database);
            store.Delete(Assert.Single(store.ForPerson("p_them")).Id);

            Assert.Empty(store.ForPerson("p_them"));
            Assert.Equal(2, save.Count("SELECT count(*) FROM fact WHERE source = 'user_deleted';"));
        }
    }

    /// <summary>A person with nothing to merge gets no job; one with something gets one, once.</summary>
    [Fact]
    public void Merging_is_queued_only_for_people_with_candidates_and_only_once()
    {
        var (save, first, second, _) = Seeded();

        using (save)
        {
            var jobs = new AiJobs(save.Database);
            using var runner = new AiRunner(jobs, new AiState(new AiSettingsStore(_directory)), []);
            var work = new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner, new FactMerger(save.Database));

            Fact(save, "f1", "works_at", "Acme", first);

            Assert.Equal(0, work.PlanMerging());

            Fact(save, "f2", "works_at", "Acme Corp", second);

            Assert.Equal(1, work.PlanMerging());

            // Asking again finds the job it already has rather than adding a second.
            work.PlanMerging();
            Assert.Equal(1, save.Count("SELECT count(*) FROM ai_job;"));

            // Done at this plan, nothing new: nothing to queue.
            jobs.Complete(jobs.Claim()!.Id);
            Assert.Equal(0, work.PlanMerging());
        }
    }

    [Theory]
    [InlineData("Acme", "acme.", true)]
    [InlineData("Acme", "Acme Corp", true)]
    [InlineData("Berlin", "Berlin, Germany", true)]
    [InlineData("the cinema", "cinema", true)]
    [InlineData("Berlin", "Prague", false)]
    [InlineData("two", "three", false)]
    public void Values_that_look_alike_are_asked_about(string a, string b, bool similar)
    {
        Assert.Equal(similar, FactMerger.Similar(a, b));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
