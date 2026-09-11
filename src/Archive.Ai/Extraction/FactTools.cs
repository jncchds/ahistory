using Archive.Ai.Llm;

namespace Archive.Ai.Extraction;

/// <summary>
/// The tools an extraction model may call, and the only way anything reaches the fact store.
/// </summary>
/// <remarks>
/// <para>
/// The model does not return a document to be parsed and trusted. It calls these, each call is
/// validated on its own, and an invalid one comes back as a tool result it can correct. Three
/// reasons that is the right shape here: a malformed blob fails wholesale while a bad call fails
/// alone; the model finds out that message 8231 is not in this window while it can still fix it;
/// and the arguments <b>are</b> the audit record, one call to one row.
/// </para>
/// <para>
/// <b>The schemas are deliberately flat.</b> Every argument is a string, a number, or an array of
/// one of those — no nested objects, no arrays of objects, no <c>minItems</c>. A local runtime
/// compiles a tool schema into a decoding grammar, and the nested shape these started as
/// (<c>subject: {person | pair}</c>, <c>asserts: [{message_id, quote}]</c>) made llama.cpp abort
/// with <i>bad allocation</i> and take the model server down with it. A pair is therefore two
/// optional person arguments and citations are a flat list of ids — less elegant to read, and the
/// difference between working on a local model and not.
/// </para>
/// <para>
/// <b>Two things the model is deliberately not asked for.</b> Whether a line was said in a DM or a
/// group is a property of the thread, and whether a claim is self-reported or reflected follows
/// from who sent the cited message relative to the subject. The runner derives both. Asking would
/// add two fields the model can get wrong and nothing it can get right.
/// </para>
/// </remarks>
public static class FactTools
{
    public const string RecordFact = "record_fact";
    public const string CorroborateFact = "corroborate_fact";
    public const string ContradictFact = "contradict_fact";
    public const string SupersedeFact = "supersede_fact";
    public const string NothingToRecord = "nothing_to_record";

    private const string MessageIds = """
        "message_ids": {
          "type": "array",
          "description": "Ids of the messages this comes from. Only ids shown in the transcript.",
          "items": { "type": "integer" }
        }
        """;

    /// <summary>Everything the model may do, in the order it should think about them.</summary>
    public static IReadOnlyList<LlmToolDefinition> All { get; } =
    [
        new(
            RecordFact,
            "Record something you learned about a person, or about the relationship between two "
            + "people. Only what was actually said — not what they discussed.",
            $$"""
              {
                "type": "object",
                "properties": {
                  "subject_person": {
                    "type": "string",
                    "description": "The person id this is about, from the roster."
                  },
                  "subject_person_b": {
                    "type": "string",
                    "description": "Only for something true of a relationship rather than of either person: the second person id."
                  },
                  "predicate": {
                    "type": "string",
                    "description": "A short lowercase key, English, underscores: works_at, lives_in, has_child, studies."
                  },
                  "object": { "type": "string", "description": "The value, short. 'Acme', 'Berlin', 'two'." },
                  "claim": { "type": "string", "description": "One readable sentence stating the fact." },
                  "confidence": { "type": "number", "description": "Between 0 and 1." },
                  {{MessageIds}},
                  "quote": { "type": "string", "description": "The words it was found in, if you can point at them." },
                  "valid_from": { "type": "string", "description": "ISO date, when this became true, if the messages say." },
                  "valid_from_message_id": { "type": "integer", "description": "The message that says so." },
                  "valid_to": { "type": "string", "description": "ISO date, when this stopped being true, if the messages say." },
                  "valid_to_message_id": { "type": "integer", "description": "The message that says so." }
                },
                "required": ["subject_person", "predicate", "object", "claim", "confidence", "message_ids"]
              }
              """),
        new(
            CorroborateFact,
            "Say that a fact you were shown as already known is supported by something in this "
            + "conversation.",
            $$"""
              {
                "type": "object",
                "properties": {
                  "fact_id": { "type": "string" },
                  {{MessageIds}}
                },
                "required": ["fact_id", "message_ids"]
              }
              """),
        new(
            ContradictFact,
            "Say that something in this conversation disagrees with a fact you were shown. "
            + "Disagreement is recorded, not resolved.",
            $$"""
              {
                "type": "object",
                "properties": {
                  "fact_id": { "type": "string" },
                  {{MessageIds}},
                  "note": { "type": "string", "description": "What disagrees, in one sentence." }
                },
                "required": ["fact_id", "message_ids"]
              }
              """),
        new(
            SupersedeFact,
            "Replace a fact you were shown with a new value, because it has changed. Use this "
            + "rather than recording a second, contradictory fact — 'works at Acme' from 2019 is "
            + "not wrong, it has expired.",
            $$"""
              {
                "type": "object",
                "properties": {
                  "fact_id": { "type": "string" },
                  "object": { "type": "string", "description": "The new value." },
                  "claim": { "type": "string", "description": "One readable sentence stating the new fact." },
                  "confidence": { "type": "number", "description": "Between 0 and 1." },
                  {{MessageIds}},
                  "valid_from": { "type": "string", "description": "ISO date the new value started, if the messages say." },
                  "valid_from_message_id": { "type": "integer" }
                },
                "required": ["fact_id", "object", "claim", "confidence", "message_ids"]
              }
              """),
        new(
            NothingToRecord,
            "This conversation contains nothing worth recording. This is a correct and common "
            + "outcome — most conversations are arrangements, and recording them is worse than "
            + "recording nothing.",
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
