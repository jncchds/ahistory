using System.Net;
using System.Text.Json;
using Archive.Ai.Llm;

namespace Archive.Ai.Tests;

/// <summary>
/// What goes on the wire, and what comes back off it.
/// </summary>
public sealed class ProviderTests
{
    private static AiSettings Settings(string endpoint = "http://localhost:1234/v1") => new()
    {
        Enabled = true,
        Endpoint = endpoint,
        MainModel = "test-model",
        MaxRetries = 0,
        RetryBaseDelayMs = 1,
    };

    private static LlmChatRequest Ask(params LlmChatMessage[] messages) => new()
    {
        Model = "test-model",
        Messages = messages,
    };

    private const string OneWord = """
        {"choices":[{"message":{"content":"ready"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":7,"completion_tokens":1,"total_tokens":8}}
        """;

    [Fact]
    public async Task A_configured_endpoint_wins_over_the_family_default()
    {
        var endpoint = new FakeEndpoint().Answers(OneWord);
        var provider = new OpenAiProvider(Settings("http://192.168.0.9:1234/v1/"), endpoint);

        await provider.ChatAsync(Ask(LlmChatMessage.User("hello")));

        Assert.Equal(
            "http://192.168.0.9:1234/v1/chat/completions", endpoint.Requests[0].Uri.ToString());
    }

    /// <summary>
    /// A blank key omits the header rather than sending an empty one.
    /// </summary>
    /// <remarks>
    /// Some local servers reject <c>Authorization: Bearer</c> with nothing after it, which would
    /// make "leave the key empty for a local model" advice that does not work.
    /// </remarks>
    [Fact]
    public async Task A_blank_key_sends_no_authorization_header()
    {
        var endpoint = new FakeEndpoint().Answers(OneWord);

        await new OpenAiCompatibleProvider(Settings(), endpoint)
            .ChatAsync(Ask(LlmChatMessage.User("hello")));

        Assert.Null(endpoint.Requests[0].Authorization);
    }

    [Fact]
    public async Task A_key_is_sent_as_a_bearer_token()
    {
        var endpoint = new FakeEndpoint().Answers(OneWord);
        var settings = Settings();
        settings.ApiKey = "  sk-abc  ";

        await new OpenAiCompatibleProvider(settings, endpoint)
            .ChatAsync(Ask(LlmChatMessage.User("hello")));

        Assert.Equal("Bearer sk-abc", endpoint.Requests[0].Authorization);
    }

    [Fact]
    public async Task Usage_and_finish_reason_come_back_from_a_completion()
    {
        var endpoint = new FakeEndpoint().Answers(OneWord);

        var completion = await new OpenAiCompatibleProvider(Settings(), endpoint)
            .ChatAsync(Ask(LlmChatMessage.User("hello")));

        Assert.Equal("ready", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(8, completion.TotalTokens);
        Assert.Empty(completion.ToolCalls);
    }

    /// <summary>
    /// Tools reach the wire as the provider expects them, schema and all.
    /// </summary>
    /// <remarks>
    /// The schema is written as JSON in the source and has to arrive as JSON, not as a string
    /// containing JSON — a provider handed the latter reports no usable tools and answers in
    /// prose, which is indistinguishable from a model that cannot call tools at all.
    /// </remarks>
    [Fact]
    public async Task A_tool_definition_is_sent_as_a_function_with_a_parsed_schema()
    {
        var endpoint = new FakeEndpoint().Answers(OneWord);

        var request = Ask(LlmChatMessage.User("go")) with
        {
            Tools = [new LlmToolDefinition(
                "record_fact",
                "Record something learned.",
                """{"type":"object","properties":{"claim":{"type":"string"}}}""")],
            ToolChoice = LlmToolChoice.Required,
        };

        await new OpenAiCompatibleProvider(Settings(), endpoint).ChatAsync(request);

        using var sent = JsonDocument.Parse(endpoint.Requests[0].Body);
        var tool = sent.RootElement.GetProperty("tools")[0];

        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("record_fact", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(
            JsonValueKind.Object,
            tool.GetProperty("function").GetProperty("parameters").ValueKind);
        Assert.Equal("required", sent.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task Tool_calls_are_read_off_a_completion()
    {
        var endpoint = new FakeEndpoint().Answers("""
            {"choices":[{"message":{"content":null,"tool_calls":[
                {"id":"call_1","type":"function",
                 "function":{"name":"record_fact","arguments":"{\"claim\":\"moved to Berlin\"}"}}]},
              "finish_reason":"tool_calls"}]}
            """);

        var completion = await new OpenAiCompatibleProvider(Settings(), endpoint)
            .ChatAsync(Ask(LlmChatMessage.User("go")));

        var call = Assert.Single(completion.ToolCalls);

        Assert.Equal("call_1", call.Id);
        Assert.Equal("record_fact", call.Name);
        Assert.Contains("Berlin", call.ArgumentsJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool result carries the id of the call it answers.
    /// </summary>
    /// <remarks>
    /// Without it the provider rejects the whole conversation, which surfaces as a 400 on the
    /// second round of every extraction rather than as anything to do with tools.
    /// </remarks>
    [Fact]
    public async Task A_tool_result_is_sent_with_the_call_id_it_answers()
    {
        var endpoint = new FakeEndpoint().Answers(OneWord);

        await new OpenAiCompatibleProvider(Settings(), endpoint).ChatAsync(Ask(
            LlmChatMessage.User("go"),
            LlmChatMessage.Assistant(null, [new LlmToolCall("call_1", "record_fact", "{}")]),
            LlmChatMessage.ToolResult("call_1", "recorded")));

        using var sent = JsonDocument.Parse(endpoint.Requests[0].Body);
        var messages = sent.RootElement.GetProperty("messages");

        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task Embeddings_come_back_in_the_order_the_provider_indexed_them()
    {
        var endpoint = new FakeEndpoint().Answers("""
            {"data":[{"index":1,"embedding":[0.5,0.5]},{"index":0,"embedding":[0.1,0.2]}]}
            """);

        var vectors = await new OpenAiCompatibleProvider(Settings(), endpoint)
            .EmbedAsync("embed-model", ["first", "second"]);

        Assert.Equal(2, vectors.Count);
        Assert.Equal(0.1f, vectors[0][0]);
        Assert.Equal(0.5f, vectors[1][0]);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_a_client_error_is_not()
    {
        var flaky = new FakeEndpoint()
            .Answers("busy", HttpStatusCode.TooManyRequests)
            .Answers(OneWord);

        var settings = Settings();
        settings.MaxRetries = 2;

        await new OpenAiCompatibleProvider(settings, flaky).ChatAsync(Ask(LlmChatMessage.User("hi")));

        Assert.Equal(2, flaky.Requests.Count);

        var refused = new FakeEndpoint()
            .Answers("""{"error":{"message":"no such model"}}""", HttpStatusCode.BadRequest);

        var failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            new OpenAiCompatibleProvider(settings, refused).ChatAsync(Ask(LlmChatMessage.User("hi"))));

        Assert.Equal(400, failure.HttpStatus);
        Assert.Contains("no such model", failure.ErrorPayload!, StringComparison.Ordinal);
        Assert.Single(refused.Requests);
    }

    [Fact]
    public async Task Cancelling_is_not_retried()
    {
        var endpoint = new FakeEndpoint().Answers(OneWord);
        var settings = Settings();
        settings.MaxRetries = 5;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenAiCompatibleProvider(settings, endpoint)
                .ChatAsync(Ask(LlmChatMessage.User("hi")), cancelled.Token));

        Assert.Empty(endpoint.Requests);
    }

    [Fact]
    public async Task An_endpoint_that_lists_no_models_is_not_an_error()
    {
        var endpoint = new FakeEndpoint().Answers("""{"object":"list"}""");

        var models = await new OpenAiCompatibleProvider(Settings(), endpoint).ListModelsAsync();

        Assert.Empty(models);
    }

    [Fact]
    public async Task Models_are_listed_by_id()
    {
        var endpoint = new FakeEndpoint().Answers("""
            {"data":[{"id":"zeta"},{"id":"alpha"},{"nope":true}]}
            """);

        var models = await new OpenAiCompatibleProvider(Settings(), endpoint).ListModelsAsync();

        Assert.Equal(["alpha", "zeta"], models.Select(m => m.Id));
    }

    /// <summary>Never answers: a stand-in for a model still being read in from disk.</summary>
    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            throw new InvalidOperationException("Never reached: the delay only ends by cancellation.");
        }
    }

    /// <summary>
    /// A timeout says the model may still be loading, and where to give it longer.
    /// </summary>
    /// <remarks>
    /// A local model on a hard drive can take minutes to answer the first call after it was
    /// unloaded. "Did not answer within the timeout" on its own sends people looking for a network
    /// fault that is not there.
    /// </remarks>
    [Fact]
    public async Task A_timeout_says_the_model_may_still_be_loading()
    {
        var settings = Settings();
        settings.TimeoutMs = 50;

        var failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            new OpenAiCompatibleProvider(settings, new Silent()).ChatAsync(Ask(LlmChatMessage.User("hi"))));

        Assert.Contains("loading", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Wait for an answer", failure.Message, StringComparison.Ordinal);
    }
}
