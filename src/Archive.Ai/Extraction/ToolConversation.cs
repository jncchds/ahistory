using Archive.Ai.Llm;

namespace Archive.Ai.Extraction;

/// <summary>How a tool conversation ended.</summary>
/// <param name="CalledAnything">False when the model answered in prose instead of calling a tool.</param>
/// <param name="ModelVersion">What the endpoint said it ran, or "unknown".</param>
internal sealed record ToolConversationResult(bool CalledAnything, string ModelVersion);

/// <summary>
/// The loop every write path shares: show the material, take the tool calls, answer each one, and
/// let the model fix what was refused.
/// </summary>
/// <remarks>
/// <para>
/// Extraction, merging, the diary and the rollups all write through tools, and all of them want the
/// same things from the conversation around those tools: a bounded number of corrections, an
/// honest record of which model ran, and nothing written until it is over. One loop is what keeps
/// those from drifting into four slightly different ones.
/// </para>
/// <para>
/// Nothing here touches the database. The dispatcher stages; the caller commits afterwards, in one
/// short transaction, so no model call is ever inside one (P1).
/// </para>
/// </remarks>
internal static class ToolConversation
{
    /// <summary>
    /// How many times the model may be told it got a call wrong.
    /// </summary>
    /// <remarks>
    /// Bounded because a model that cannot satisfy the validator will not start being able to on
    /// the ninth attempt, and every round is a paid call over the same material.
    /// </remarks>
    public const int CorrectionRounds = 2;

    /// <param name="dispatch">Handles one call by name and raw arguments; its reply goes back verbatim.</param>
    /// <param name="finished">
    /// True once the model has said, in as many words, that it is done — after which another round
    /// would only invite it to invent something.
    /// </param>
    public static async Task<ToolConversationResult> RunAsync(
        AiClient client,
        AiSettings settings,
        string model,
        string system,
        string material,
        IReadOnlyList<LlmToolDefinition> tools,
        Func<string, string, ToolReply> dispatch,
        Func<bool> finished,
        AiPurpose purpose,
        AiSubject subject,
        CancellationToken cancellationToken)
    {
        var conversation = new List<LlmChatMessage>
        {
            LlmChatMessage.System(system),
            LlmChatMessage.User(material),
        };

        var calledAnything = false;

        // Most endpoints report nothing, and "unknown" is the honest record: a made-up version
        // would make "re-run what the old model did" a query that cannot tell two models apart.
        var modelVersion = "unknown";

        for (var round = 0; round <= CorrectionRounds; round++)
        {
            var completion = await client.ChatAsync(
                settings,
                new LlmChatRequest
                {
                    Model = model,
                    Messages = conversation,
                    Tools = tools,
                    ToolChoice = LlmToolChoice.Auto,
                    Temperature = settings.Temperature,
                    MaxTokens = settings.MaxTokens,
                },
                purpose,
                subject,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(completion.SystemFingerprint))
            {
                modelVersion = completion.SystemFingerprint;
            }

            if (completion.ToolCalls.Count == 0)
            {
                break;
            }

            calledAnything = true;
            conversation.Add(LlmChatMessage.Assistant(completion.Content, completion.ToolCalls));

            var refused = 0;

            foreach (var call in completion.ToolCalls)
            {
                var reply = dispatch(call.Name, call.ArgumentsJson);

                if (!reply.Accepted)
                {
                    refused++;
                }

                conversation.Add(LlmChatMessage.ToolResult(call.Id, reply.Message));
            }

            if (refused == 0 || finished())
            {
                break;
            }

            conversation.Add(LlmChatMessage.User(
                "Some of those calls were refused, with the reason after each. Send corrected "
                + "calls for those, and nothing for the ones that were accepted."));
        }

        return new ToolConversationResult(calledAnything, modelVersion);
    }
}
