using System.Text.Json;
using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Search;
using Archive.Ai.Sessions;
using Archive.Data;

namespace Archive.Ai.Tests;

/// <summary>
/// Sessions embedded, found by meaning, and a model changed without search ever going away (A6).
/// </summary>
public sealed class EmbeddingTests : IDisposable
{
    private const long Noon = 1_700_000_000;
    private const long Day = 86_400;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Two conversations ten days apart: a trip, and a knee.</summary>
    private static TempSave Seeded()
    {
        var save = new TempSave();

        save.Seed(
            "t1",
            (Noon, "we should plan the trip to Prague in spring, I found cheap tickets"),
            (Noon + 60, "yes let us book it before the prices go up"),
            (Noon + (10 * Day), "my knee still hurts after the long run yesterday"),
            (Noon + (10 * Day) + 60, "you should see a doctor about that knee"));

        new SessionSegmenter(save.Database).SegmentThread("t1");
        save.Execute("UPDATE session SET is_substantive = 1;");

        return save;
    }

    private AiState Consented(FakeEndpoint endpoint, string embeddingModel = "nomic-embed")
    {
        var settings = new AiSettings
        {
            Enabled = true,
            Endpoint = "http://localhost:1234/v1",
            MainModel = "test-model",
            EmbeddingModel = embeddingModel,
            MaxRetries = 0,
            RetryBaseDelayMs = 1,
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        };

        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, new LlmProviderFactory(endpoint));

        var state = new AiState(new AiSettingsStore(_directory));
        state.Update(settings);

        return state;
    }

    private static string Vectors(params float[][] vectors) => JsonSerializer.Serialize(new
    {
        data = vectors.Select((v, i) => new { index = i, embedding = v }),
    });

    private string SessionAbout(TempSave save, string word) =>
        save.Text($"SELECT session_id FROM message WHERE plaintext LIKE '%{word}%' LIMIT 1;")!;

    private async Task Embed(TempSave save, FakeEndpoint endpoint, AiState state)
    {
        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);
        var handler = new EmbedJobHandler(
            new EmbeddingStore(save.Database), new SessionText(new ExtractionWindows(save.Database)),
            client, state, new LlmProviderFactory(endpoint));

        await handler.HandleAsync(new AiJob(1, AiJobKind.Embed, "thread", "t1", AiJobState.Running, 0, 1, "h"), default);
    }

    [Fact]
    public async Task A_threads_sessions_are_embedded_in_one_call_and_stored_normalized()
    {
        using var save = Seeded();

        // Newest first: the knee, then the trip.
        var endpoint = new FakeEndpoint().Answers(Vectors([0, 3, 0], [4, 0, 0]));
        var state = Consented(endpoint);

        await Embed(save, endpoint, state);

        Assert.Single(endpoint.Requests);
        Assert.Contains("search_document: ", endpoint.Requests[0].Body, StringComparison.Ordinal);

        var store = new EmbeddingStore(save.Database);
        var coverage = store.Coverage("nomic-embed");

        Assert.Equal(2, coverage.Eligible);
        Assert.Equal(2, coverage.Embedded);

        // Unit length, whatever the endpoint sent.
        foreach (var (_, vector) in store.Vectors("nomic-embed"))
        {
            Assert.Equal(1f, MathF.Sqrt(vector.Sum(v => v * v)), 3);
        }

        // Nothing is left to do for this model.
        Assert.Empty(store.ThreadsNeeding("nomic-embed"));
    }

    [Fact]
    public async Task A_query_finds_the_session_nearest_in_meaning()
    {
        using var save = Seeded();

        var endpoint = new FakeEndpoint()
            .Answers(Vectors([0, 1, 0], [1, 0, 0]))
            .Answers(Vectors([0.9f, 0.1f, 0]));

        var state = Consented(endpoint);
        await Embed(save, endpoint, state);

        var semantic = new SemanticSearch(
            new EmbeddingStore(save.Database),
            new AiClient(new LlmProviderFactory(endpoint), save.Interactions),
            state,
            new LlmProviderFactory(endpoint));

        var hits = await semantic.SearchAsync("a journey abroad", 5);

        Assert.Equal(SessionAbout(save, "Prague"), hits[0].SessionId);
        Assert.Contains("search_query: a journey abroad", endpoint.Requests[1].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// §10: a hit with no word in common is marked as found by meaning; one found both ways says so.
    /// </summary>
    [Fact]
    public async Task Hybrid_results_say_which_half_found_them()
    {
        using var save = Seeded();

        var endpoint = new FakeEndpoint()
            .Answers(Vectors([0, 1, 0], [1, 0, 0]))
            .Answers(Vectors([1, 0, 0]))
            .Answers(Vectors([1, 0, 0]));

        var state = Consented(endpoint);
        await Embed(save, endpoint, state);

        var factory = new LlmProviderFactory(endpoint);
        var hybrid = new HybridSearch(
            save.Database,
            new ArchiveSearch(save.Database),
            new SemanticSearch(new EmbeddingStore(save.Database), new AiClient(factory, save.Interactions), state, factory));

        var journey = await hybrid.SearchAsync("journey");

        var meaning = Assert.Single(journey);
        Assert.Equal("meaning", meaning.FoundBy);
        Assert.Contains("Prague", meaning.Snippet[0].Text, StringComparison.Ordinal);

        var prague = await hybrid.SearchAsync("Prague");

        Assert.Equal("both", prague[0].FoundBy);
    }

    /// <summary>
    /// §9.2: a new model is built beside the old one, and search moves across only once it is complete.
    /// </summary>
    [Fact]
    public void Search_stays_on_the_old_model_until_the_new_one_is_complete()
    {
        using var save = Seeded();

        var store = new EmbeddingStore(save.Database);
        var trip = SessionAbout(save, "Prague");
        var knee = SessionAbout(save, "knee");

        store.Write("old", [(trip, "h", [1, 0]), (knee, "h", [0, 1])]);

        Assert.Equal("old", store.Active("new"));

        store.Write("new", [(trip, "h", [1, 0, 0])]);

        Assert.Equal("old", store.Active("new"));

        store.Write("new", [(knee, "h", [0, 1, 0])]);

        Assert.Equal("new", store.Active("new"));

        // Reverting the setting halfway would make the old model active again at once.
        Assert.Equal("old", store.Active("old"));

        // And clearing unused vectors keeps both the configured and the active model.
        Assert.Equal(0, store.ClearUnused("new") - 2);
        Assert.Equal("new", Assert.Single(store.Models()).Model);
    }

    /// <summary>Someone left out is not embedded: their conversation is not sent to an embedding endpoint either.</summary>
    [Fact]
    public void A_conversation_with_someone_left_out_is_never_embedded()
    {
        using var save = Seeded();

        new AiExclusions(save.Database).Set("p_them", excluded: true);

        var store = new EmbeddingStore(save.Database);

        Assert.Empty(store.ThreadsNeeding("nomic-embed"));
        Assert.Equal(0, store.Coverage("nomic-embed").Eligible);
        Assert.Null(new SessionText(new ExtractionWindows(save.Database)).For(SessionAbout(save, "knee"), "nomic-embed"));
    }

    /// <summary>§10: zero coverage and no agreement are both silent — nothing is offered, nothing sent.</summary>
    [Fact]
    public async Task Without_agreement_search_by_meaning_is_unavailable_and_sends_nothing()
    {
        using var save = Seeded();

        var endpoint = new FakeEndpoint();
        var state = new AiState(new AiSettingsStore(_directory));
        state.Update(new AiSettings
        {
            Enabled = true,
            MainModel = "m",
            EmbeddingModel = "e",
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        });

        new EmbeddingStore(save.Database).Write("e", [(SessionAbout(save, "knee"), "h", [1, 0])]);

        var factory = new LlmProviderFactory(endpoint);
        var semantic = new SemanticSearch(new EmbeddingStore(save.Database), new AiClient(factory, save.Interactions), state, factory);

        Assert.False(semantic.Availability().IsAvailable);
        Assert.Empty(await semantic.SearchAsync("knee", 5));
        Assert.Empty(endpoint.Requests);
    }

    [Fact]
    public void Embedding_is_queued_per_thread_and_not_again_once_done()
    {
        using var save = Seeded();

        var endpoint = new FakeEndpoint();
        var state = Consented(endpoint);
        var jobs = new AiJobs(save.Database);
        var store = new EmbeddingStore(save.Database);

        using var runner = new AiRunner(jobs, state, []);
        var work = new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner, embeddings: store, state: state);

        Assert.Equal(1, work.PlanEmbeddings());

        store.Write("nomic-embed",
        [
            (SessionAbout(save, "Prague"), EmbeddingStore.InputHash(save.Text($"SELECT member_hash FROM session WHERE id = '{SessionAbout(save, "Prague")}';")!), [1, 0]),
            (SessionAbout(save, "knee"), EmbeddingStore.InputHash(save.Text($"SELECT member_hash FROM session WHERE id = '{SessionAbout(save, "knee")}';")!), [0, 1]),
        ]);

        Assert.Empty(store.ThreadsNeeding("nomic-embed"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
