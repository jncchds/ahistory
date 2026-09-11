using System.Net;
using Archive.Ai.Llm;

namespace Archive.Ai.Tests;

/// <summary>
/// Test connection has to catch the failure that otherwise costs an evening: a model that answers
/// perfectly and cannot call a tool, which produces a run that writes nothing at all.
/// </summary>
public sealed class AiConnectionCheckTests
{
    private const string Prose = """
        {"choices":[{"message":{"content":"ready"},"finish_reason":"stop"}]}
        """;

    private const string ToolCall = """
        {"choices":[{"message":{"content":null,"tool_calls":[
            {"id":"c1","type":"function","function":{"name":"report_ready","arguments":"{\"ready\":true}"}}]},
          "finish_reason":"tool_calls"}]}
        """;

    private static AiSettings Settings() => new()
    {
        Enabled = true,
        Endpoint = "http://localhost:1234/v1",
        MainModel = "test-model",
        MaxRetries = 0,
        RetryBaseDelayMs = 1,
    };

    private static AiConnectionCheck Check(TempSave save, FakeEndpoint endpoint) =>
        new(new AiClient(new LlmProviderFactory(endpoint), save.Interactions));

    [Fact]
    public async Task A_model_that_answers_and_calls_a_tool_is_usable()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers(Prose).Answers(ToolCall);

        var result = await Check(save, endpoint).RunAsync(Settings());

        Assert.True(result.Reached);
        Assert.True(result.SupportsTools);
        Assert.True(result.IsUsable);
    }

    /// <summary>
    /// Answering in prose when required to call a tool is the failure this exists for.
    /// </summary>
    [Fact]
    public async Task A_model_that_will_not_call_a_tool_is_reached_but_not_usable()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers(Prose).Answers(Prose);

        var result = await Check(save, endpoint).RunAsync(Settings());

        Assert.True(result.Reached);
        Assert.False(result.SupportsTools);
        Assert.False(result.IsUsable);
        Assert.Contains("tool calling", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reasoning model whose whole answer was thinking is still a model that answered.
    /// </summary>
    /// <remarks>
    /// Found against a real one: it spends tokens on reasoning the wire format carries in a field
    /// this does not read, so <c>content</c> comes back empty. Calling that "answered, but said
    /// nothing" sent the user to check an endpoint that was working perfectly.
    /// </remarks>
    [Fact]
    public async Task A_model_that_answers_with_nothing_but_reasoning_is_still_reached()
    {
        using var save = new TempSave();

        var silent = """
            {"choices":[{"message":{"content":"","reasoning_content":"thinking…"},
              "finish_reason":"length"}]}
            """;

        var result = await Check(save, new FakeEndpoint().Answers(silent).Answers(ToolCall))
            .RunAsync(Settings());

        Assert.True(result.Reached);
        Assert.True(result.IsUsable);
    }

    [Fact]
    public async Task An_endpoint_that_cannot_be_reached_says_so_rather_than_throwing()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint()
            .Answers("""{"error":{"message":"model not found"}}""", HttpStatusCode.NotFound);

        var result = await Check(save, endpoint).RunAsync(Settings());

        Assert.False(result.Reached);
        Assert.Contains("model not found", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_model_is_refused_without_a_call()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint();
        var settings = Settings();
        settings.MainModel = "  ";

        var result = await Check(save, endpoint).RunAsync(settings);

        Assert.False(result.Reached);
        Assert.Empty(endpoint.Requests);
    }

    /// <summary>Both calls of the probe are recorded, like every other call.</summary>
    [Fact]
    public async Task The_probe_shows_up_in_the_statistics()
    {
        using var save = new TempSave();
        var endpoint = new FakeEndpoint().Answers(Prose).Answers(ToolCall);

        await Check(save, endpoint).RunAsync(Settings());

        Assert.Equal(2, save.Count("SELECT count(*) FROM ai_interaction WHERE purpose = 'test';"));
    }
}
