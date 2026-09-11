using System.Text.Json;
using Archive.Ai.Diary;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// The diary: a month from its facts, every sentence with its messages, silence said out loud, and
/// the past never quietly rewritten (A5, spec §8).
/// </summary>
public sealed class DiaryTests : IDisposable
{
    /// <summary>10 March 2023 and 10 May 2023, with nothing in April.</summary>
    private const long March = 1_678_406_400;
    private const long May = 1_683_676_800;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private static AiSettings Settings() => new()
    {
        Enabled = true,
        Endpoint = "http://localhost:1234/v1",
        MainModel = "test-model",
        OutputLanguage = "English",
        MaxRetries = 0,
        RetryBaseDelayMs = 1,
        DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
    };

    /// <summary>A conversation in March and one in May, each with a fact read out of it.</summary>
    private static (TempSave Save, long MarchMessage, long MayMessage) Seeded()
    {
        var save = new TempSave();

        save.Seed(
            "t1",
            (March, "I started at Acme today, first day went well"),
            (March + 60, "that is wonderful news, tell me everything"),
            (May, "we are moving to Prague in the summer"),
            (May + 60, "Prague! that is a big change for you both"));

        new SessionSegmenter(save.Database).SegmentThread("t1");
        save.Execute("UPDATE session SET is_substantive = 1;");

        var first = save.Count("SELECT min(id) FROM message;");

        Fact(save, "f1", "p_them", "works_at", "Acme", "Them started at Acme.", first);
        Fact(save, "f2", "p_them", "moving_to", "Prague", "Them is moving to Prague.", first + 2);

        return (save, first, first + 2);
    }

    private static void Fact(
        TempSave save, string id, string person, string predicate, string value, string claim, long messageId)
    {
        save.Execute($$"""
            INSERT OR IGNORE INTO derived_artifact (id, kind, engine, model, model_version, payload_json,
                                                    input_hash, created_utc)
                VALUES ('a1', 'session_extract', 'llm', 'm', 'unknown', '{}', 'h', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO fact (id, subject_person_id, predicate, object_text, claim_text, evidence_kind,
                              origin_kind, confidence, asserted_utc, derived_artifact_id)
                VALUES ('{{id}}', '{{person}}', '{{predicate}}', '{{value}}', '{{claim}}', 'self_report',
                        'dm', 0.9, '2026-01-01T00:00:00.0000000Z', 'a1');
            INSERT INTO fact_citation (fact_id, message_id, role) VALUES ('{{id}}', {{messageId}}, 'asserts');
            """);
    }

    /// <summary>A completion that writes sentences, each citing the ids given.</summary>
    private static string Sentences(params (string Text, long[] Ids)[] sentences)
    {
        var calls = sentences.Select((s, i) => new
        {
            id = $"c{i}",
            type = "function",
            function = new
            {
                name = "write_sentence",
                arguments = JsonSerializer.Serialize(new { text = s.Text, message_ids = s.Ids }),
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

    private static string Nothing() => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new
            {
                message = new
                {
                    content = (string?)null,
                    tool_calls = new[]
                    {
                        new { id = "c0", type = "function", function = new { name = "nothing_to_write", arguments = "{\"reason\":\"logistics\"}" } },
                    },
                },
                finish_reason = "tool_calls",
            },
        },
    });

    private static (DiaryRunner Runner, DiaryStore Store) Diary(TempSave save, FakeEndpoint endpoint)
    {
        var inputs = new DiaryInputs(save.Database);
        var store = new DiaryStore(save.Database, inputs);
        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);

        return (new DiaryRunner(save.Database, inputs, store, client), store);
    }

    [Fact]
    public async Task A_month_is_written_from_its_facts_with_every_sentence_cited()
    {
        var (save, marchMessage, _) = Seeded();

        using (save)
        {
            var endpoint = new FakeEndpoint().Answers(Sentences(("They started at Acme.", [marchMessage])));
            var (runner, store) = Diary(save, endpoint);

            var outcome = await runner.MonthAsync(Settings(), "p_them", 2023, 3, "h1");

            Assert.Equal(DiaryOutcome.Written, outcome);

            var month = Assert.Single(store.ForPerson("p_them").Months);
            var sentence = Assert.Single(month.Latest.Sentences);

            Assert.Equal("They started at Acme.", sentence.Text);
            Assert.Equal([marchMessage], sentence.MessageIds);

            // The model was shown the month's fact and the conversation it came from — and not May's.
            Assert.Contains("Them started at Acme.", endpoint.Requests[0].Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Prague", endpoint.Requests[0].Body, StringComparison.Ordinal);
        }
    }

    /// <summary>§8: a sentence nothing stands behind is dropped, and the model is told while it can fix it.</summary>
    [Fact]
    public async Task A_sentence_citing_a_message_it_was_not_shown_is_refused()
    {
        var (save, marchMessage, mayMessage) = Seeded();

        using (save)
        {
            var endpoint = new FakeEndpoint()
                .Answers(Sentences(("They moved to Prague.", [mayMessage])))
                .Answers(Sentences(("They started at Acme.", [marchMessage])));

            var (runner, store) = Diary(save, endpoint);

            await runner.MonthAsync(Settings(), "p_them", 2023, 3, "h1");

            Assert.Contains("was not shown above", endpoint.Requests[1].Body, StringComparison.Ordinal);
            Assert.Equal("They started at Acme.", Assert.Single(Assert.Single(store.ForPerson("p_them").Months).Latest.Sentences).Text);
        }
    }

    /// <summary>§8: silence is content, so it is given to the model rather than left to be noticed.</summary>
    [Fact]
    public async Task The_months_of_silence_before_an_entry_are_stated()
    {
        var (save, _, mayMessage) = Seeded();

        using (save)
        {
            var endpoint = new FakeEndpoint().Answers(Sentences(("They are moving to Prague.", [mayMessage])));
            var (runner, store) = Diary(save, endpoint);

            await runner.MonthAsync(Settings(), "p_them", 2023, 5, "h1");

            Assert.Contains("one month without a single message", endpoint.Requests[0].Body, StringComparison.Ordinal);
            Assert.Equal(1, Assert.Single(store.ForPerson("p_them").Months).Latest.QuietMonthsBefore);
        }
    }

    [Fact]
    public async Task Nothing_to_write_is_a_conclusion_and_is_recorded()
    {
        var (save, _, _) = Seeded();

        using (save)
        {
            var (runner, store) = Diary(save, new FakeEndpoint().Answers(Nothing()));

            Assert.Equal(DiaryOutcome.NothingToWrite, await runner.MonthAsync(Settings(), "p_them", 2023, 3, "h1"));

            var month = Assert.Single(store.ForPerson("p_them").Months);

            Assert.True(month.Latest.NothingToWrite);
            Assert.Empty(month.Latest.Sentences);
        }
    }

    /// <summary>
    /// "A diary that silently rewrites your past every time you open it is unsettling" — so a
    /// regenerated month is a new row, and the old one is kept as what it replaced.
    /// </summary>
    [Fact]
    public async Task A_rewritten_month_is_a_revision_and_the_old_text_is_kept()
    {
        var (save, marchMessage, _) = Seeded();

        using (save)
        {
            var endpoint = new FakeEndpoint()
                .Answers(Sentences(("They started at Acme.", [marchMessage])))
                .Answers(Sentences(("They began a new job at Acme.", [marchMessage])));

            var (runner, store) = Diary(save, endpoint);

            await runner.MonthAsync(Settings(), "p_them", 2023, 3, "h1");
            await runner.MonthAsync(Settings(), "p_them", 2023, 3, "h2");

            var month = Assert.Single(store.ForPerson("p_them").Months);

            Assert.Equal(2, month.Revisions);
            Assert.Equal("They began a new job at Acme.", month.Latest.Sentences[0].Text);
            Assert.Equal("They started at Acme.", month.Previous!.Sentences[0].Text);
        }
    }

    /// <summary>Someone left out has no diary, and nothing about them is sent to be written.</summary>
    [Fact]
    public async Task Someone_left_out_has_no_diary()
    {
        var (save, marchMessage, _) = Seeded();

        using (save)
        {
            new AiExclusions(save.Database).Set("p_them", excluded: true);

            var endpoint = new FakeEndpoint().Answers(Sentences(("They started at Acme.", [marchMessage])));
            var (runner, _) = Diary(save, endpoint);

            Assert.Equal(DiaryOutcome.Skipped, await runner.MonthAsync(Settings(), "p_them", 2023, 3, "h1"));
            Assert.Empty(endpoint.Requests);

            var inputs = new DiaryInputs(save.Database);
            var planner = new DiaryPlanner(save.Database, inputs, new DiaryStore(save.Database, inputs));

            Assert.Empty(planner.Months());
        }
    }

    /// <summary>The owner's month is every conversation they had; they get facts, never a transcript.</summary>
    [Fact]
    public async Task The_owners_month_is_written_from_facts_alone()
    {
        var (save, marchMessage, _) = Seeded();

        using (save)
        {
            Fact(save, "f3", "p_me", "celebrated", "a new job", "You celebrated a new job with a friend.", marchMessage + 1);
            save.Execute("INSERT INTO thread_participant (thread_id, identity_id, first_seen_unix) VALUES ('t1', 'i_me', 0);");

            var endpoint = new FakeEndpoint().Answers(Sentences(("You celebrated.", [marchMessage + 1])));
            var (runner, _) = Diary(save, endpoint);

            await runner.MonthAsync(Settings(), "p_me", 2023, 3, "h1");

            Assert.Contains("write about them as \\u0022you\\u0022", endpoint.Requests[0].Body, StringComparison.Ordinal);
            Assert.DoesNotContain("tell me everything", endpoint.Requests[0].Body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A month waits for its conversations to be read, and is not asked for again once written from
    /// the same facts.
    /// </summary>
    [Fact]
    public async Task A_month_waits_until_its_conversations_are_read_and_is_then_written_once()
    {
        var (save, marchMessage, _) = Seeded();

        using (save)
        {
            var jobs = new AiJobs(save.Database);
            var inputs = new DiaryInputs(save.Database);
            var store = new DiaryStore(save.Database, inputs);
            var planner = new DiaryPlanner(save.Database, inputs, store);

            using var runner = new AiRunner(jobs, new AiState(new AiSettingsStore(_directory)), []);
            var work = new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner, diary: planner);

            // March's conversation is still waiting to be read.
            var marchSession = save.Text($"SELECT session_id FROM message WHERE id = {marchMessage};")!;
            jobs.Enqueue(AiJobKind.Extract, "session", marchSession, "h");

            Assert.DoesNotContain(planner.Months(), w => w.SubjectId == "p_them|2023-03");
            Assert.Contains(planner.Months(), w => w.SubjectId == "p_them|2023-05");

            jobs.Complete(jobs.Claim()!.Id);

            var march = Assert.Single(planner.Months(), w => w.SubjectId == "p_them|2023-03");

            Assert.True(work.PlanDiary() > 0);

            // Written, and done at this key: asking again queues nothing new.
            var (diary, _) = Diary(save, new FakeEndpoint().Answers(Sentences(("They started at Acme.", [marchMessage]))));
            await diary.MonthAsync(Settings(), "p_them", 2023, 3, march.InputHash);

            foreach (var job in Enumerable.Range(0, 2).Select(_ => jobs.Claim()).OfType<AiJob>())
            {
                jobs.Complete(job.Id);
            }

            Assert.Equal(0, jobs.Counts(AiJobKind.Diary).Pending);
            work.PlanDiary();
            Assert.Equal(0, jobs.Counts(AiJobKind.Diary).Pending);
        }
    }

    /// <summary>§6.4: a year is written from its months, and cites only what they cite.</summary>
    [Fact]
    public async Task A_year_is_written_from_its_months_and_cites_what_they_cite()
    {
        var (save, marchMessage, mayMessage) = Seeded();

        using (save)
        {
            var endpoint = new FakeEndpoint()
                .Answers(Sentences(("They started at Acme.", [marchMessage])))
                .Answers(Sentences(("They are moving to Prague.", [mayMessage])))
                .Answers(Sentences(("A year of a new job and a planned move.", [marchMessage, mayMessage])));

            var (runner, store) = Diary(save, endpoint);

            await runner.MonthAsync(Settings(), "p_them", 2023, 3, "h1");
            await runner.MonthAsync(Settings(), "p_them", 2023, 5, "h2");

            var inputs = new DiaryInputs(save.Database);
            var planner = new DiaryPlanner(save.Database, inputs, store);
            var year = Assert.Single(planner.Summaries(), w => w.SubjectId == "p_them|2023");

            Assert.Equal(DiaryOutcome.Written, await runner.YearAsync(Settings(), "p_them", 2023, year.InputHash));

            // Built from the month entries, not from the messages: the transcript never reaches it.
            Assert.Contains("They started at Acme.", endpoint.Requests[2].Body, StringComparison.Ordinal);
            Assert.DoesNotContain("tell me everything", endpoint.Requests[2].Body, StringComparison.Ordinal);
            Assert.Single(store.ForPerson("p_them").Years);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
