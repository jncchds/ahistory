using System.Text.Json;
using Archive.Ai.Extraction;
using Archive.Ai.Llm;
using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// A session, a model that calls tools, and what ends up in the archive.
/// </summary>
public sealed class ExtractionTests : IDisposable
{
    private const long Noon = 1_700_000_000;

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

    /// <summary>A completion that asks for one tool call.</summary>
    private static string Calls(params (string Tool, string Arguments)[] calls)
    {
        var items = calls.Select((call, i) => new
        {
            id = $"c{i}",
            type = "function",
            function = new { name = call.Tool, arguments = call.Arguments },
        });

        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = (string?)null, tool_calls = items }, finish_reason = "tool_calls" },
            },
        });
    }

    private static string Prose(string text) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = text }, finish_reason = "stop" } },
    });

    /// <summary>A save with one session, ready to be read.</summary>
    private static (TempSave Save, string SessionId) Seeded()
    {
        var save = new TempSave();

        save.Seed(
            "t1",
            (Noon, "I started at Acme last week, finally"),
            (Noon + 60, "congratulations, that is excellent news"));

        new SessionSegmenter(save.Database).SegmentThread("t1");

        return (save, save.Text("SELECT id FROM session;")!);
    }

    private ExtractRunner Runner(TempSave save, params string[] answers)
    {
        var client = new AiClient(
            new LlmProviderFactory(Answering(answers)), new AiInteractions(save.Database));

        return new ExtractRunner(
            client, new ExtractionWindows(save.Database), new FactWriter(save.Database));
    }

    private static FakeEndpoint Answering(string[] answers)
    {
        var endpoint = new FakeEndpoint();

        foreach (var answer in answers)
        {
            endpoint.Answers(answer);
        }

        return endpoint;
    }

    [Fact]
    public async Task A_fact_the_model_records_lands_with_its_citations()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var messageId = save.Count("SELECT min(id) FROM message;");

            var report = await Runner(save, Calls((FactTools.RecordFact, $$"""
                {"subject_person":"p_them","predicate":"works_at","object":"Acme",
                 "claim":"Sam started at Acme.","confidence":0.9,
                 "message_ids":[{{messageId}}],"quote":"I started at Acme"}
                """)))
                .RunAsync(Settings(), sessionId, "hash-1");

            Assert.Equal(ExtractionOutcome.Written, report.Outcome);
            Assert.Equal(1, report.Result!.Facts);

            Assert.Equal("Sam started at Acme.", save.Text("SELECT claim_text FROM fact;"));
            Assert.Equal("self_report", save.Text("SELECT evidence_kind FROM fact;"));
            Assert.Equal("dm", save.Text("SELECT origin_kind FROM fact;"));
            Assert.Equal("asserts", save.Text("SELECT role FROM fact_citation;"));
            Assert.Equal(1, save.Count("SELECT count(*) FROM derived_artifact WHERE kind = 'session_extract';"));
        }
    }

    /// <summary>
    /// "Nothing here" is recorded, because it is a conclusion.
    /// </summary>
    /// <remarks>
    /// Writing the artifact is what stops the session being read again at the same prompt version.
    /// Without it, every logistics session in the archive is re-read on every pass — which is most
    /// of the archive, and most of the bill.
    /// </remarks>
    [Fact]
    public async Task A_session_with_nothing_in_it_is_recorded_as_having_nothing_in_it()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var report = await Runner(save, Calls((FactTools.NothingToRecord, """{"reason":"arrangements"}""")))
                .RunAsync(Settings(), sessionId, "hash-1");

            Assert.Equal(ExtractionOutcome.NothingToRecord, report.Outcome);
            Assert.Equal(0, save.Count("SELECT count(*) FROM fact;"));
            Assert.Equal(1, save.Count("SELECT count(*) FROM derived_artifact;"));
        }
    }

    /// <summary>
    /// A refused call comes back as its own result, and the model gets to fix it.
    /// </summary>
    /// <remarks>
    /// The reason the write path is tool calls rather than one JSON document: a bad call fails
    /// alone and is correctable in place, while a malformed document fails the whole session.
    /// </remarks>
    [Fact]
    public async Task A_refused_call_is_corrected_on_the_next_round()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var messageId = save.Count("SELECT min(id) FROM message;");

            var report = await Runner(
                save,
                Calls((FactTools.RecordFact, """
                    {"subject_person":"p_them","predicate":"works_at","object":"Acme",
                     "claim":"Sam started at Acme.","confidence":0.9,
                     "message_ids":[999999]}
                    """)),
                Calls((FactTools.RecordFact, $$"""
                    {"subject_person":"p_them","predicate":"works_at","object":"Acme",
                     "claim":"Sam started at Acme.","confidence":0.9,
                     "message_ids":[{{messageId}}]}
                    """)))
                .RunAsync(Settings(), sessionId, "hash-1");

            Assert.Equal(ExtractionOutcome.Written, report.Outcome);
            Assert.Equal(1, save.Count("SELECT count(*) FROM fact;"));
        }
    }

    /// <summary>
    /// A model that answers in prose is a session to look at, not a session to forget.
    /// </summary>
    [Fact]
    public async Task A_model_that_calls_nothing_leaves_the_session_for_review()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var report = await Runner(save, Prose("I think Sam has a new job."))
                .RunAsync(Settings(), sessionId, "hash-1");

            Assert.Equal(ExtractionOutcome.NeedsReview, report.Outcome);
            Assert.Equal(0, save.Count("SELECT count(*) FROM fact;"));
            Assert.Equal(0, save.Count("SELECT count(*) FROM derived_artifact;"));
        }
    }

    /// <summary>
    /// A conversation the user excluded is never read, and never sent.
    /// </summary>
    [Fact]
    public async Task An_excluded_thread_is_not_read_at_all()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            save.Execute("UPDATE thread SET ai_excluded = 1;");

            var endpoint = new FakeEndpoint();
            var client = new AiClient(new LlmProviderFactory(endpoint), new AiInteractions(save.Database));

            var report = await new ExtractRunner(
                    client, new ExtractionWindows(save.Database), new FactWriter(save.Database))
                .RunAsync(Settings(), sessionId, "hash-1");

            Assert.Equal(ExtractionOutcome.Skipped, report.Outcome);

            // Not merely unrecorded: nothing left the machine.
            Assert.Empty(endpoint.Requests);
        }
    }

    /// <summary>A save that opted out is not read on any machine, however this one is configured.</summary>
    [Fact]
    public async Task A_save_that_opted_out_is_not_read()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            save.Execute("""
                INSERT INTO save_meta (id, owner_is_self, created_utc, ai_opt_out)
                VALUES (1, 1, '2026-01-01T00:00:00.0000000Z', 1)
                ON CONFLICT (id) DO UPDATE SET ai_opt_out = 1;
                """);

            var report = await Runner(save, Calls((FactTools.NothingToRecord, "{}")))
                .RunAsync(Settings(), sessionId, "hash-1");

            Assert.Equal(ExtractionOutcome.Skipped, report.Outcome);
        }
    }

    /// <summary>
    /// A second run at a new prompt version stops believing the first, and never touches a correction.
    /// </summary>
    /// <remarks>
    /// Assertion time, not event time: the old facts stop being believed, they do not stop having
    /// been believed. And a fact the user edited survives — a correction retracted by the next
    /// prompt improvement is what would make the facts panel not worth correcting.
    /// </remarks>
    [Fact]
    public async Task A_rerun_retracts_what_it_replaces_but_not_what_the_user_touched()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var messageId = save.Count("SELECT min(id) FROM message;");

            string Fact(string value) => $$"""
                {"subject_person":"p_them","predicate":"works_at","object":"{{value}}",
                 "claim":"Sam works at {{value}}.","confidence":0.9,
                 "message_ids":[{{messageId}}]}
                """;

            await Runner(save, Calls((FactTools.RecordFact, Fact("Acme"))))
                .RunAsync(Settings(), sessionId, "hash-1");

            // As if the user had corrected one of them.
            save.Execute("UPDATE fact SET source = 'user_edited';");

            await Runner(save, Calls((FactTools.RecordFact, Fact("Globex"))))
                .RunAsync(Settings(), sessionId, "hash-2");

            Assert.Equal(2, save.Count("SELECT count(*) FROM fact;"));
            Assert.Equal(0, save.Count("SELECT count(*) FROM fact WHERE retracted_utc IS NOT NULL;"));

            // A plain extracted fact from the first run would have been retracted; this one is the
            // user's, so it stands.
            Assert.Equal(
                1, save.Count("SELECT count(*) FROM fact WHERE source = 'user_edited' AND retracted_utc IS NULL;"));
        }
    }

    [Fact]
    public async Task Superseding_closes_the_old_fact_and_dates_it()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var messageId = save.Count("SELECT min(id) FROM message;");

            await Runner(save, Calls((FactTools.RecordFact, $$"""
                {"subject_person":"p_them","predicate":"lives_in","object":"Berlin",
                 "claim":"Sam lives in Berlin.","confidence":0.9,
                 "message_ids":[{{messageId}}]}
                """)))
                .RunAsync(Settings(), sessionId, "hash-1");

            var oldId = save.Text("SELECT id FROM fact;")!;

            // A later run, at a later prompt version, replacing it.
            await Runner(save, Calls((FactTools.SupersedeFact, $$$"""
                {"fact_id":"{{{oldId}}}","object":"Vienna","claim":"Sam has moved to Vienna.",
                 "confidence":0.8,"message_ids":[{{{messageId}}}],
                 "valid_from":"2024-03-01","valid_from_message_id":{{{messageId}}}}
                """)))
                .RunAsync(Settings(), sessionId, "hash-2");

            Assert.Equal(
                1, save.Count($"SELECT count(*) FROM fact WHERE id = '{oldId}' AND superseded_by IS NOT NULL;"));

            Assert.StartsWith(
                "2024-03-01",
                save.Text($"SELECT valid_to_utc FROM fact WHERE id = '{oldId}';")!,
                StringComparison.Ordinal);
        }
    }

    /// <summary>A pair fact creates the edge it belongs to, once.</summary>
    [Fact]
    public async Task A_fact_about_a_pair_lands_on_an_edge()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var messageId = save.Count("SELECT min(id) FROM message;");

            var call = (FactTools.RecordFact, $$"""
                {"subject_person":"p_them","subject_person_b":"p_me","predicate":"met_in","object":"Berlin",
                 "claim":"They met in Berlin.","confidence":0.7,
                 "message_ids":[{{messageId}}]}
                """);

            await Runner(save, Calls(call)).RunAsync(Settings(), sessionId, "hash-1");
            await Runner(save, Calls(call)).RunAsync(Settings(), sessionId, "hash-2");

            Assert.Equal(1, save.Count("SELECT count(*) FROM person_edge;"));
            Assert.Equal(2, save.Count("SELECT count(*) FROM fact WHERE subject_edge_id IS NOT NULL;"));
            Assert.Equal(0, save.Count("SELECT count(*) FROM fact WHERE subject_person_id IS NOT NULL;"));
        }
    }

    /// <summary>
    /// A correction survives the session it came from ceasing to exist.
    /// </summary>
    /// <remarks>
    /// The data-loss path this closes looked entirely reasonable: a thread gains one message, is
    /// re-segmented, and the session the message landed in is replaced — and because artifacts
    /// cascade from sessions and facts cascade from artifacts, every fact read from that session
    /// went with it, the user's own corrections included. Now the model's facts are retracted and
    /// the user's stay exactly as they were.
    /// </remarks>
    [Fact]
    public async Task A_correction_survives_its_session_being_resegmented()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var messageId = save.Count("SELECT min(id) FROM message;");

            string Fact(string value) => $$"""
                {"subject_person":"p_them","predicate":"works_at","object":"{{value}}",
                 "claim":"Sam works at {{value}}.","confidence":0.9,"message_ids":[{{messageId}}]}
                """;

            await Runner(save, Calls((FactTools.RecordFact, Fact("Acme")), (FactTools.RecordFact, Fact("Globex"))))
                .RunAsync(Settings(), sessionId, "hash-1");

            Assert.Equal(2, save.Count("SELECT count(*) FROM fact;"));

            // The user corrects one of them.
            save.Execute("UPDATE fact SET source = 'user_edited' WHERE object_text = 'Acme';");

            // A later import adds a message to the same conversation, close enough in time to
            // extend the session — which gives it a new identity and retires the old one.
            save.Seed("t1", (Noon + 120, "and the commute is only ten minutes"));
            new SessionSegmenter(save.Database).SegmentThread("t1");

            Assert.NotEqual(sessionId, save.Text("SELECT id FROM session;"));

            // Nothing was deleted. The model's reading of the old session is no longer believed;
            // the user's correction is exactly as it was.
            Assert.Equal(2, save.Count("SELECT count(*) FROM fact;"));
            Assert.Equal(
                1, save.Count("SELECT count(*) FROM fact WHERE object_text = 'Globex' AND retracted_utc IS NOT NULL;"));
            Assert.Equal(
                1, save.Count("SELECT count(*) FROM fact WHERE object_text = 'Acme' AND retracted_utc IS NULL;"));

            // And its citations still point at the messages it came from.
            Assert.Equal(2, save.Count("SELECT count(*) FROM fact_citation;"));
        }
    }

    /// <summary>Extracts one fact about Sam from the seeded session and returns its id.</summary>
    private async Task<string> OneFact(TempSave save, string sessionId, string value = "Acme")
    {
        var messageId = save.Count("SELECT min(id) FROM message;");

        await Runner(save, Calls((FactTools.RecordFact, $$"""
            {"subject_person":"p_them","predicate":"works_at","object":"{{value}}",
             "claim":"Sam works at {{value}}.","confidence":0.7,"message_ids":[{{messageId}}]}
            """)))
            .RunAsync(Settings(), sessionId, $"hash-{value}");

        return save.Text($"SELECT id FROM fact WHERE object_text = '{value}' AND retracted_utc IS NULL;")!;
    }

    /// <summary>
    /// An edit is a new row that replaces the old one, and keeps what it was read from.
    /// </summary>
    /// <remarks>
    /// Append-only, like every other change to a fact: the model's version was believed until the
    /// user corrected it, and that has to stay answerable. The citations come across because the
    /// correction is about what those same messages say.
    /// </remarks>
    [Fact]
    public async Task Editing_a_fact_supersedes_it_and_keeps_its_evidence()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var original = await OneFact(save, sessionId);
            var store = new FactStore(save.Database);

            var corrected = store.Edit(original, "Acme Robotics", "Sam works at Acme Robotics.");

            var live = Assert.Single(store.ForPerson("p_them"));

            Assert.Equal(corrected, live.Id);
            Assert.Equal("Sam works at Acme Robotics.", live.ClaimText);
            Assert.Equal("user_edited", live.Source);
            Assert.Equal(1.0, live.Confidence);
            Assert.NotNull(live.FirstMessageId);

            Assert.Equal(corrected, save.Text($"SELECT superseded_by FROM fact WHERE id = '{original}';"));
            Assert.Equal(1, save.Count($"SELECT count(*) FROM fact_citation WHERE fact_id = '{corrected}';"));

            // Editing the old row again is refused rather than forking the history.
            Assert.Throws<InvalidOperationException>(() => store.Edit(original, "x", "x"));
        }
    }

    [Fact]
    public async Task Deleting_a_fact_leaves_a_tombstone_that_the_panel_does_not_show()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var fact = await OneFact(save, sessionId);
            var store = new FactStore(save.Database);

            store.Delete(fact);

            Assert.Empty(store.ForPerson("p_them"));
            Assert.Equal(1, save.Count("SELECT count(*) FROM fact WHERE source = 'user_deleted';"));
        }
    }

    /// <summary>
    /// What the user removed stays removed when the model reads it again.
    /// </summary>
    /// <remarks>
    /// The failure this closes is the one that would make correcting the panel pointless: delete
    /// a wrong fact, improve the prompt, re-run — and it is back. Instead the re-run's reading is
    /// filed as a citation against the tombstone, so the evidence is kept and the verdict stands.
    /// </remarks>
    [Fact]
    public async Task A_deleted_fact_is_not_brought_back_by_a_later_run()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var fact = await OneFact(save, sessionId);

            new FactStore(save.Database).Delete(fact);

            // A later run, at a new input hash, that reads exactly the same thing.
            var messageId = save.Count("SELECT min(id) FROM message;");

            var report = await Runner(save, Calls((FactTools.RecordFact, $$"""
                {"subject_person":"p_them","predicate":"works_at","object":"ACME",
                 "claim":"Sam works at Acme.","confidence":0.9,"message_ids":[{{messageId}}]}
                """)))
                .RunAsync(Settings(), sessionId, "hash-again");

            Assert.Equal(0, report.Result!.Facts);
            Assert.Empty(new FactStore(save.Database).ForPerson("p_them"));
            Assert.Equal(
                1, save.Count($"SELECT count(*) FROM fact_citation WHERE fact_id = '{fact}' AND role = 'corroborates';"));
        }
    }

    [Fact]
    public async Task Coverage_counts_what_a_model_has_read_at_this_prompt()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            // Two short lines, below the filter on their own; marked worth reading so that the
            // count is about extraction and not about the filter's thresholds.
            save.Execute("UPDATE session SET is_substantive = 1;");

            var coverage = new AiCoverage(save.Database);

            Assert.Equal(0, coverage.Summary().Extracted);

            await OneFact(save, sessionId);

            Assert.Equal(1, coverage.Summary().Extracted);
            Assert.Equal(1.0, coverage.Summary().ExtractedFraction);
            Assert.Equal(1, coverage.ForPerson("p_them").Extracted);

            // Read under another prompt version is work to do again, not coverage.
            save.Execute("UPDATE derived_artifact SET prompt_version = '0';");

            Assert.Equal(0, coverage.Summary().Extracted);
        }
    }

    /// <summary>The transcript the model is shown carries the ids it is told to cite.</summary>
    [Fact]
    public void The_window_shows_real_message_ids()
    {
        var (save, sessionId) = Seeded();

        using (save)
        {
            var window = new ExtractionWindows(save.Database).Load(sessionId)!;

            Assert.Equal(2, window.Messages.Count);
            Assert.Contains($"[{window.Messages[0].Id}]", window.Transcript(), StringComparison.Ordinal);
            Assert.Contains("Them", window.Transcript(), StringComparison.Ordinal);
            Assert.False(window.IsGroup);
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
