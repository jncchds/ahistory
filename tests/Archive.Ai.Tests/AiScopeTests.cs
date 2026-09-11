using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// What is not to be read, and taking back what was.
/// </summary>
public sealed class AiScopeTests : IDisposable
{
    private const long Noon = 1_700_000_000;
    private const long Hour = 3_600;

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

    private static string Seeded(TempSave save)
    {
        save.Seed(
            "t1",
            (Noon, "I started at Acme last week, finally"),
            (Noon + 60, "congratulations, that is excellent news"));

        new SessionSegmenter(save.Database).SegmentThread("t1");
        save.Execute("UPDATE session SET is_substantive = 1;");

        return save.Text("SELECT id FROM session;")!;
    }

    private static ExtractRunner Runner(TempSave save, FakeEndpoint endpoint) =>
        new(
            new AiClient(new LlmProviderFactory(endpoint), save.Interactions),
            new ExtractionWindows(save.Database),
            new FactWriter(save.Database));

    /// <summary>
    /// Someone left out has their direct conversations neither read nor sent.
    /// </summary>
    /// <remarks>
    /// Before this, excluding a person only took them off the roster — their direct conversation
    /// was still read and sent, just with nobody allowed to be its subject. "Not analysed" has to
    /// mean "not sent" as well, or the exclusion is a label rather than a promise.
    /// </remarks>
    [Fact]
    public async Task A_person_left_out_has_their_direct_conversations_never_read()
    {
        using var save = new TempSave();
        var sessionId = Seeded(save);

        new AiExclusions(save.Database).Set("p_them", excluded: true);

        var endpoint = new FakeEndpoint();
        var report = await Runner(save, endpoint).RunAsync(Settings(), sessionId, "h");

        Assert.Equal(ExtractionOutcome.Skipped, report.Outcome);
        Assert.Empty(endpoint.Requests);
    }

    [Fact]
    public void A_person_left_out_has_nothing_queued_for_them()
    {
        using var save = new TempSave();
        Seeded(save);

        new AiExclusions(save.Database).Set("p_them", excluded: true);

        var jobs = new AiJobs(save.Database);
        using var runner = new AiRunner(jobs, new AiState(new AiSettingsStore(_directory)), []);

        Assert.Equal(0, new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner).PlanExtraction());
    }

    /// <summary>
    /// In a group, their lines are dropped before the transcript goes anywhere — and only theirs.
    /// </summary>
    [Fact]
    public void In_a_group_their_lines_are_left_out_of_what_is_sent()
    {
        using var save = new TempSave();
        var sessionId = Seeded(save);

        save.Execute("UPDATE thread SET kind = 'group';");
        save.Execute("UPDATE message SET sender_identity_id = 'i_me' WHERE plaintext LIKE 'congratulations%';");

        new AiExclusions(save.Database).Set("p_them", excluded: true);

        var window = new ExtractionWindows(save.Database).Load(sessionId)!;

        Assert.DoesNotContain("Acme", window.Transcript(), StringComparison.Ordinal);
        Assert.Contains("congratulations", window.Transcript(), StringComparison.Ordinal);
        Assert.DoesNotContain(window.People, p => p.Id == "p_them");
    }

    /// <summary>
    /// Letting someone back in brings back what was skipped while they were out.
    /// </summary>
    /// <remarks>
    /// A skipped session is marked done with nothing read. Without reviving it, asking for the work
    /// again finds the job it already has, and the conversation is never read at all.
    /// </remarks>
    [Fact]
    public void Letting_someone_back_in_brings_back_what_was_skipped()
    {
        using var save = new TempSave();
        var sessionId = Seeded(save);

        var jobs = new AiJobs(save.Database);
        var exclusions = new AiExclusions(save.Database);

        exclusions.Set("p_them", excluded: true);
        jobs.Enqueue(AiJobKind.Extract, "session", sessionId, "h");
        jobs.Complete(jobs.Claim()!.Id);

        exclusions.Set("p_them", excluded: false);

        Assert.False(exclusions.IsExcluded("p_them"));
        Assert.Equal(1, jobs.Counts(AiJobKind.Extract).Pending);
    }

    /// <summary>
    /// Forgetting removes everything the AI layer produced — and not one message.
    /// </summary>
    [Fact]
    public async Task Forgetting_removes_everything_derived_and_nothing_else()
    {
        using var save = new TempSave();
        var sessionId = Seeded(save);

        var messageId = save.Count("SELECT min(id) FROM message;");
        var endpoint = new FakeEndpoint().Answers($$$"""
            {"choices":[{"message":{"content":null,"tool_calls":[{"id":"c1","type":"function",
             "function":{"name":"record_fact","arguments":"{\"subject_person\":\"p_them\",\"subject_person_b\":\"p_me\",\"predicate\":\"works_at\",\"object\":\"Acme\",\"claim\":\"x\",\"confidence\":0.7,\"message_ids\":[{{{messageId}}}]}"}}]},
             "finish_reason":"tool_calls"}]}
            """);

        await Runner(save, endpoint).RunAsync(Settings(), sessionId, "h");
        new AiJobs(save.Database).Enqueue(AiJobKind.Extract, "session", sessionId, "h");
        new AiExclusions(save.Database).Set("p_me", excluded: true);

        var messages = save.Count("SELECT count(*) FROM message;");
        var forget = new AiForget(save.Database);
        var before = forget.Counts();

        Assert.False(before.IsEmpty);

        var removed = forget.Everything();

        Assert.Equal(before, removed);
        Assert.True(forget.Counts().IsEmpty);

        foreach (var table in new[] { "fact", "fact_citation", "derived_artifact", "person_edge", "ai_job", "ai_interaction", "session" })
        {
            Assert.Equal(0, save.Count($"SELECT count(*) FROM {table};"));
        }

        // The archive itself is untouched, and so is the user's choice about who is left out.
        Assert.Equal(messages, save.Count("SELECT count(*) FROM message;"));
        Assert.Equal(0, save.Count("SELECT count(*) FROM message WHERE session_id IS NOT NULL;"));
        Assert.True(new AiExclusions(save.Database).IsExcluded("p_me"));
    }

    /// <summary>
    /// §9: in someone else's archive, what others said about a person is not recorded — only what
    /// people said about themselves.
    /// </summary>
    [Fact]
    public async Task In_someone_elses_archive_only_what_people_say_about_themselves_is_recorded()
    {
        using var save = new TempSave();
        var sessionId = Seeded(save);

        save.Execute("INSERT INTO save_meta (id, owner_is_self, created_utc) VALUES (1, 0, '2026-01-01T00:00:00Z');");

        var said = save.Count("SELECT min(id) FROM message;");
        var replied = said + 1;

        // The reply is theirs to nobody but the owner — a reflected claim about "them".
        save.Execute($"UPDATE message SET sender_identity_id = 'i_me' WHERE id = {replied};");

        var endpoint = new FakeEndpoint().Answers($$$"""
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"c1","type":"function","function":{"name":"record_fact","arguments":"{\"subject_person\":\"p_them\",\"predicate\":\"mood\",\"object\":\"happy\",\"claim\":\"x\",\"confidence\":0.6,\"message_ids\":[{{{replied}}}]}"}},
              {"id":"c2","type":"function","function":{"name":"record_fact","arguments":"{\"subject_person\":\"p_them\",\"predicate\":\"works_at\",\"object\":\"Acme\",\"claim\":\"y\",\"confidence\":0.9,\"message_ids\":[{{{said}}}]}"}}]},
             "finish_reason":"tool_calls"}]}
            """);

        await Runner(save, endpoint).RunAsync(Settings(), sessionId, "h");

        Assert.Equal("works_at", save.Text("SELECT predicate FROM fact;"));
        Assert.Equal(1, save.Count("SELECT count(*) FROM fact;"));
    }

    [Fact]
    public async Task A_conversation_left_out_is_never_read_and_is_read_once_let_back_in()
    {
        using var save = new TempSave();
        var sessionId = Seeded(save);
        var exclusions = new AiExclusions(save.Database);

        exclusions.SetThread("t1", excluded: true);

        var endpoint = new FakeEndpoint();

        Assert.Equal(ExtractionOutcome.Skipped, (await Runner(save, endpoint).RunAsync(Settings(), sessionId, "h")).Outcome);
        Assert.Empty(endpoint.Requests);

        var jobs = new AiJobs(save.Database);
        jobs.Enqueue(AiJobKind.Extract, "session", sessionId, "h");
        jobs.Complete(jobs.Claim()!.Id);

        exclusions.SetThread("t1", excluded: false);

        Assert.False(exclusions.IsThreadExcluded("t1"));
        Assert.Equal(1, jobs.Counts(AiJobKind.Extract).Pending);
    }

    /// <summary>§9: the save's own answer wins over the machine's, and travels with the file.</summary>
    [Fact]
    public void An_archive_that_opted_out_has_nothing_queued_on_any_machine()
    {
        using var save = new TempSave();
        Seeded(save);

        var exclusions = new AiExclusions(save.Database);
        exclusions.SetSaveOptOut(true);

        Assert.True(exclusions.SaveOptedOut());

        var jobs = new AiJobs(save.Database);
        using var runner = new AiRunner(jobs, new AiState(new AiSettingsStore(_directory)), []);

        Assert.Equal(0, new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner).PlanExtraction());

        exclusions.SetSaveOptOut(false);

        Assert.False(exclusions.SaveOptedOut());
        Assert.Equal(1, new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner).PlanExtraction());
    }

    /// <summary>
    /// The daily budget holds model work — and only model work — once the day's calls reach it.
    /// </summary>
    [Fact]
    public async Task Reaching_the_daily_budget_holds_model_work_and_lets_local_work_carry_on()
    {
        using var save = new TempSave();
        save.Seed("t1", (Noon, "hello"));

        var state = new AiState(new AiSettingsStore(_directory));
        state.Update(new AiSettings
        {
            Enabled = true,
            MainModel = "m",
            DailyTokenBudget = 500,
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        });

        save.Interactions.Record(new AiInteraction
        {
            Purpose = AiPurpose.Extract,
            Provider = LlmProviderKind.OpenAiCompatible,
            Endpoint = "http://localhost/",
            Model = "m",
            DurationMs = 1,
            TotalTokens = 600,
        });

        var jobs = new AiJobs(save.Database);
        var model = new Counting(AiJobKind.Extract, usesModel: true);
        var local = new Counting(AiJobKind.Segment, usesModel: false);

        jobs.Enqueue(AiJobKind.Extract, "session", "s1", "h");
        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");

        using var runner = new AiRunner(jobs, state, [model, local], budget: new AiBudget(save.Interactions));

        Assert.True(runner.IsOverBudget);

        await runner.DrainAsync();

        Assert.Equal(0, model.Handled);
        Assert.Equal(1, local.Handled);
        Assert.Equal(1, jobs.Counts(AiJobKind.Extract).Pending);

        // Raising the budget lets it through, with no attempt spent while it waited.
        state.Update(state.Current.Clone() is var raised && (raised.DailyTokenBudget = 10_000) > 0 ? raised : raised);

        await runner.DrainAsync();

        Assert.Equal(1, model.Handled);
    }

    /// <summary>
    /// Failed is visible, not final: work that ran out of attempts can be put back, once asked.
    /// </summary>
    [Fact]
    public void Failed_work_stays_failed_until_retried_and_then_starts_afresh()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);

        jobs.Enqueue(AiJobKind.Diary, "person", "p_them|2023-03", "h");

        for (var attempt = 1; attempt <= AiJobs.MaxAttempts; attempt++)
        {
            jobs.Fail(jobs.Claim()!.Id, "http_400", attempt);
        }

        Assert.Equal(1, jobs.Counts().Failed);

        // Asking for the same work again does not bring it back on its own.
        jobs.Enqueue(AiJobKind.Diary, "person", "p_them|2023-03", "h");
        Assert.Equal(1, jobs.Counts().Failed);

        Assert.Equal(1, jobs.RetryFailed());

        var retried = jobs.Claim()!;

        Assert.Equal(1, retried.Attempts);
        Assert.Equal(0, jobs.Counts().Failed);
    }

    private sealed class Counting(AiJobKind kind, bool usesModel) : IAiJobHandler
    {
        public int Handled { get; private set; }

        public AiJobKind Kind => kind;

        public bool UsesModel => usesModel;

        public Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
        {
            Handled++;

            return Task.FromResult(AiJobOutcome.Done);
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
