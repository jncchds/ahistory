using Archive.Ai.Llm;

namespace Archive.Ai;

/// <summary>What Test connection found.</summary>
/// <param name="Reached">The endpoint answered with a completion.</param>
/// <param name="SupportsTools">The model returned a tool call when required to.</param>
/// <param name="Detail">One sentence for the settings page, whether it worked or not.</param>
public sealed record AiCheckResult(bool Reached, bool SupportsTools, string Detail)
{
    /// <summary>True when this model can actually run the pipeline.</summary>
    public bool IsUsable => Reached && SupportsTools;
}

/// <summary>
/// Two calls, because reaching a model and being able to use it are different questions.
/// </summary>
/// <remarks>
/// <para>
/// The whole write path is tool calls: extraction does not return a document to be parsed, it
/// calls <c>record_fact</c>. A model without tool calling therefore produces a run that looks
/// completely healthy — requests succeed, tokens are spent, hours pass — and writes nothing at
/// all. That failure is invisible in every log and every statistic except the fact count.
/// </para>
/// <para>
/// So the check asks for a tool call and insists on one. Finding out here costs two seconds;
/// finding out afterwards costs an evening, and the user has no way to tell it from a bug.
/// </para>
/// </remarks>
public sealed class AiConnectionCheck(AiClient client)
{
    /// <summary>
    /// The probe tool.
    /// </summary>
    /// <remarks>
    /// One required argument, because a model that emits a call with no arguments proves less
    /// than one that had to fill something in — and some endpoints accept a tools array, ignore
    /// it, and answer in prose, which is the case this is here to catch.
    /// </remarks>
    private static readonly LlmToolDefinition Ping = new(
        Name: "report_ready",
        Description: "Report that you are ready to work. Call this instead of answering in prose.",
        ParametersJsonSchema: """
            {
              "type": "object",
              "properties": {
                "ready": { "type": "boolean", "description": "Always true." }
              },
              "required": ["ready"]
            }
            """);

    /// <summary>
    /// Token budget for each probe call.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. A reasoning model spends tokens thinking before it emits anything at
    /// all, so a budget sized for the answer produces a truncated response with nothing in it —
    /// including no tool call, which would fail the probe for the one reason it is not testing.
    /// </remarks>
    private const int ThinkingRoom = 512;

    private readonly AiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<AiCheckResult> RunAsync(
        AiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var model = settings.ModelFor(AiWorkKind.Main);

        if (string.IsNullOrWhiteSpace(model))
        {
            return new AiCheckResult(false, false, "No model is set.");
        }

        try
        {
            // Reached means the call completed, not that the model said anything in particular.
            // A reasoning model answers with an empty `content` and its thinking in a field this
            // does not read, and a tight token budget makes that the *normal* outcome — the
            // reasoning is what the budget gets spent on. Treating that as unreachable sent people
            // to check an endpoint that was working perfectly.
            _ = await _client.ChatAsync(
                settings,
                new LlmChatRequest
                {
                    Model = model,
                    Messages = [LlmChatMessage.User("Reply with the single word: ready.")],
                    MaxTokens = ThinkingRoom,
                },
                AiPurpose.Test,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (LlmProviderException ex)
        {
            return new AiCheckResult(false, false, Explain(ex));
        }

        try
        {
            var probe = await _client.ChatAsync(
                settings,
                new LlmChatRequest
                {
                    Model = model,
                    Messages =
                    [
                        LlmChatMessage.User(
                            "Call the report_ready tool. Do not answer in prose."),
                    ],
                    Tools = [Ping],
                    ToolChoice = LlmToolChoice.Required,
                    MaxTokens = ThinkingRoom,
                },
                AiPurpose.Test,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return probe.ToolCalls.Count > 0
                ? new AiCheckResult(true, true, $"{model} answered and supports tool calling.")
                : new AiCheckResult(
                    true,
                    false,
                    $"{model} answered, but did not call the tool it was asked to call. "
                    + "Extraction needs a model that supports tool calling.");
        }
        catch (LlmProviderException ex)
        {
            // Reached, since the first call worked. Some providers reject the tools field
            // outright, which is the same answer for the user's purposes.
            return new AiCheckResult(
                true, false, $"{model} answered, but refused a tool call: {Explain(ex)}");
        }
    }

    /// <summary>
    /// The provider's own words, which are usually the only useful part.
    /// </summary>
    /// <remarks>
    /// Shown, never logged: an error body can quote the request that produced it, and a log is
    /// something people attach to bug reports (P6).
    /// </remarks>
    private static string Explain(LlmProviderException failure) =>
        string.IsNullOrWhiteSpace(failure.ErrorPayload)
            ? failure.Message
            : $"{failure.Message} {Trim(failure.ErrorPayload)}";

    private static string Trim(string payload) =>
        payload.Length <= 300 ? payload : payload[..300] + "…";
}
