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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
