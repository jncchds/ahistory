using System.Net;
using Archive.Ai.Llm;

namespace Archive.Ai.Tests;

/// <summary>
/// Every call is recorded, and what gets recorded is safe to look at.
/// </summary>
public sealed class AiClientTests
{
    private const string OneWord = """
        {"choices":[{"message":{"content":"ready"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":7,"completion_tokens":1,"total_tokens":8}}
        """;

    private static AiSettings Settings(string endpoint = "http://localhost:1234/v1") => new()
    {
        Enabled = true,
        Endpoint = endpoint,
        MainModel = "test-model",
        MaxRetries = 0,
        RetryBaseDelayMs = 1,
        DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
    };

    private static LlmChatRequest Ask() => new()
    {
        Model = "test-model",
        Messages = [LlmChatMessage.User("what did they say about Berlin?")],
    };

    [Fact]
    public async Task A_successful_call_is_recorded_with_its_tokens()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers(OneWord);
        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);

        await client.ChatAsync(Settings(), Ask(), AiPurpose.Extract, AiSubject.Session("s1"));

        Assert.Equal(1, save.Count("SELECT count(*) FROM ai_interaction WHERE failed = 0;"));
        Assert.Equal(8, save.Count("SELECT total_tokens FROM ai_interaction;"));
        Assert.Equal("extract", save.Text("SELECT purpose FROM ai_interaction;"));
        Assert.Equal("s1", save.Text("SELECT subject_id FROM ai_interaction;"));
    }

    [Fact]
    public async Task A_failed_call_is_recorded_with_the_kind_of_failure()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers("nope", HttpStatusCode.BadRequest);
        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);

        await Assert.ThrowsAsync<LlmProviderException>(() =>
            client.ChatAsync(Settings(), Ask(), AiPurpose.Extract));

        Assert.Equal(1, save.Count("SELECT count(*) FROM ai_interaction WHERE failed = 1;"));
        Assert.Equal("http_400", save.Text("SELECT failure_kind FROM ai_interaction;"));
        Assert.Equal(400, save.Count("SELECT http_status FROM ai_interaction;"));
    }

    /// <summary>
    /// The recorded endpoint carries no credential.
    /// </summary>
    /// <remarks>
    /// Some gateways take a key in the query string. Storing the endpoint verbatim would put one
    /// into a table whose whole promise is that it is safe to look at (P6).
    /// </remarks>
    [Fact]
    public async Task The_recorded_endpoint_drops_the_query_string()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers(OneWord);
        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);

        await client.ChatAsync(
            Settings("http://gateway.local/v1?key=sk-secret"), Ask(), AiPurpose.Extract);

        var recorded = save.Text("SELECT endpoint FROM ai_interaction;");

        Assert.DoesNotContain("sk-secret", recorded!, StringComparison.Ordinal);
        Assert.Equal("http://gateway.local/v1/", recorded);
    }

    /// <summary>
    /// Nothing of the conversation is stored unless the user asked for it.
    /// </summary>
    [Fact]
    public async Task Prompt_bodies_are_recorded_only_when_switched_on()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers(OneWord).Answers(OneWord);
        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);

        await client.ChatAsync(Settings(), Ask(), AiPurpose.Extract);

        Assert.Null(save.Text("SELECT request_json FROM ai_interaction;"));

        var recording = Settings();
        recording.RecordPromptBodies = true;

        await client.ChatAsync(recording, Ask(), AiPurpose.Extract);

        var body = save.Text("SELECT request_json FROM ai_interaction ORDER BY id DESC LIMIT 1;");

        Assert.NotNull(body);
        Assert.Contains("Berlin", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Statistics_group_by_purpose_and_count_failures()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint()
            .Answers(OneWord)
            .Answers(OneWord)
            .Answers("nope", HttpStatusCode.BadRequest);

        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);

        await client.ChatAsync(Settings(), Ask(), AiPurpose.Extract);
        await client.ChatAsync(Settings(), Ask(), AiPurpose.Extract);
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            client.ChatAsync(Settings(), Ask(), AiPurpose.Diary));

        var totals = save.Interactions.Totals();

        Assert.Equal(3, totals.Sum(t => t.Calls));
        Assert.Equal(1, totals.Sum(t => t.Failures));
        Assert.Equal(16, totals.Single(t => t.Purpose == AiPurpose.Extract).TotalTokens);
        Assert.Equal(("http_400", 1L), Assert.Single(save.Interactions.FailureKinds()));
    }

    [Fact]
    public async Task Clearing_statistics_removes_them_all()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers(OneWord);
        var client = new AiClient(new LlmProviderFactory(endpoint), save.Interactions);

        await client.ChatAsync(Settings(), Ask(), AiPurpose.Extract);
        save.Interactions.Clear();

        Assert.Equal(0, save.Count("SELECT count(*) FROM ai_interaction;"));
    }
}
