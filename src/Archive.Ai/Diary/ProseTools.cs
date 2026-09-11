using System.Text.Json;
using Archive.Ai.Extraction;
using Archive.Ai.Llm;

namespace Archive.Ai.Diary;

/// <summary>
/// How diary text is written: one sentence per call, each with the messages it rests on.
/// </summary>
/// <remarks>
/// <para>
/// One call per sentence rather than a <c>write_entry</c> taking a list of sentence objects, because
/// the schemas have to be flat (ai-plan.md §5.0b) and because §8's rule is per sentence — anything
/// without a citation is dropped rather than shown, and a sentence refused here is refused while
/// the model can still fix it.
/// </para>
/// <para>
/// The same two tools serve a month, a year and a profile. What differs is what they are allowed to
/// cite, which the dispatcher is given.
/// </para>
/// </remarks>
internal static class ProseTools
{
    public const string WriteSentence = "write_sentence";
    public const string NothingToWrite = "nothing_to_write";

    public static IReadOnlyList<LlmToolDefinition> All { get; } =
    [
        new(
            WriteSentence,
            "Add the next sentence, with the ids of the messages it rests on. Sentences are kept in "
            + "the order they are written.",
            """
            {
              "type": "object",
              "properties": {
                "text": { "type": "string", "description": "One sentence." },
                "message_ids": {
                  "type": "array",
                  "description": "Ids of the messages this sentence rests on. Only ids shown above.",
                  "items": { "type": "integer" }
                }
              },
              "required": ["text", "message_ids"]
            }
            """),
        new(
            NothingToWrite,
            "There is nothing here worth writing. A correct and common answer.",
            """
            {
              "type": "object",
              "properties": {
                "reason": { "type": "string", "description": "One short phrase." }
              },
              "required": ["reason"]
            }
            """),
    ];
}

/// <summary>Stages sentences, refusing any that cite nothing or cite what was not shown.</summary>
internal sealed class ProseDispatcher(IReadOnlySet<long> citable, int maxSentences)
{
    private const int MaxSentenceLength = 500;

    public List<DiarySentence> Sentences { get; } = [];

    public bool NothingToWrite { get; private set; }

    public ToolReply Dispatch(string name, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var arguments = document.RootElement;

            return name switch
            {
                ProseTools.WriteSentence => Sentence(arguments),
                ProseTools.NothingToWrite => Nothing(),
                _ => new ToolReply(false, $"There is no tool called {name}."),
            };
        }
        catch (JsonException)
        {
            return new ToolReply(false, "Those arguments were not valid JSON. Send the call again.");
        }
    }

    private ToolReply Sentence(JsonElement arguments)
    {
        if (Sentences.Count >= maxSentences)
        {
            return new ToolReply(false, "That is enough — the entry is complete. Stop here.");
        }

        var text = arguments.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()?.Trim() ?? string.Empty
            : string.Empty;

        if (text.Length == 0)
        {
            return new ToolReply(false, "text is required: one sentence.");
        }

        if (text.Length > MaxSentenceLength)
        {
            return new ToolReply(false, $"That is too long for one sentence — keep it under {MaxSentenceLength} characters.");
        }

        if (!arguments.TryGetProperty("message_ids", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return new ToolReply(false, "message_ids is required: the messages this sentence rests on.");
        }

        var ids = new List<long>();

        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetInt64(out var id))
            {
                return new ToolReply(false, "message_ids must be a list of message ids, as numbers.");
            }

            if (!citable.Contains(id))
            {
                return new ToolReply(false, $"Message {id} was not shown above. Cite only the ids you were given.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        // §8: a sentence with no message behind it is dropped, not shown. Refused here, so the
        // model is told while it can still fix it.
        if (ids.Count == 0)
        {
            return new ToolReply(false, "message_ids cannot be empty — a sentence with nothing behind it is not kept.");
        }

        Sentences.Add(new DiarySentence(text, ids));

        return new ToolReply(true, "Kept.");
    }

    private ToolReply Nothing()
    {
        NothingToWrite = true;

        return new ToolReply(true, "Understood.");
    }
}
