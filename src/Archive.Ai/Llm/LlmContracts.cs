namespace Archive.Ai.Llm;

/// <summary>Role of a message in a request.</summary>
public enum LlmRole
{
    System,
    User,
    Assistant,

    /// <summary>The result of a tool call, answering the call whose id it carries.</summary>
    Tool,
}

/// <summary>One tool call the model asked for.</summary>
/// <param name="Id">Provider-assigned; a tool result must quote it back.</param>
/// <param name="Name">Which tool.</param>
/// <param name="ArgumentsJson">Raw JSON, unparsed — the tool runtime validates it, not the transport.</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One message in a request.
/// </summary>
/// <remarks>
/// A tool conversation is a normal message list with two extra shapes in it: an assistant turn
/// that carries tool calls instead of prose, and a tool turn that answers one of them. Both have
/// to be echoed back on the next request or the provider rejects the sequence, which is why they
/// are message kinds here rather than something the caller holds on the side.
/// </remarks>
public sealed record LlmChatMessage
{
    public required LlmRole Role { get; init; }

    public string? Content { get; init; }

    /// <summary>Set on an assistant turn that asked for tools.</summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>Set on a tool turn: which call this answers.</summary>
    public string? ToolCallId { get; init; }

    public static LlmChatMessage System(string content) =>
        new() { Role = LlmRole.System, Content = content };

    public static LlmChatMessage User(string content) =>
        new() { Role = LlmRole.User, Content = content };

    public static LlmChatMessage Assistant(string? content, IReadOnlyList<LlmToolCall>? toolCalls = null) =>
        new() { Role = LlmRole.Assistant, Content = content, ToolCalls = toolCalls };

    public static LlmChatMessage ToolResult(string toolCallId, string content) =>
        new() { Role = LlmRole.Tool, ToolCallId = toolCallId, Content = content };
}

/// <summary>
/// A tool the model may call.
/// </summary>
/// <param name="Name">Snake case, matching what the runtime dispatches on.</param>
/// <param name="Description">What it does and when to use it — read by the model, so it is prompt text.</param>
/// <param name="ParametersJsonSchema">JSON Schema for the arguments object.</param>
public sealed record LlmToolDefinition(string Name, string Description, string ParametersJsonSchema);

/// <summary>What the model is allowed or required to do about tools.</summary>
public enum LlmToolChoice
{
    /// <summary>Call one if it makes sense. The normal case.</summary>
    Auto,

    /// <summary>Answer in prose; do not call anything.</summary>
    None,

    /// <summary>Must call something. Used by the capability probe, where prose is the failure.</summary>
    Required,
}

/// <summary>A provider-agnostic chat request. Every provider-specific shape is behind the interface.</summary>
public sealed record LlmChatRequest
{
    public required IReadOnlyList<LlmChatMessage> Messages { get; init; }

    public required string Model { get; init; }

    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<LlmToolDefinition>? Tools { get; init; }

    public LlmToolChoice ToolChoice { get; init; } = LlmToolChoice.Auto;
}

/// <summary>The result of a completion.</summary>
public sealed record LlmCompletion
{
    public string? Content { get; init; }

    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];

    public string? FinishReason { get; init; }

    /// <summary>
    /// What the provider says it actually ran, when it says anything.
    /// </summary>
    /// <remarks>
    /// Recorded as the model version on everything a run produces. Most endpoints report nothing,
    /// and "unknown" is the honest answer — inventing one would make §6.6's "re-run what was done
    /// with the old model" a query that cannot tell two models apart.
    /// </remarks>
    public string? SystemFingerprint { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public int? TotalTokens { get; init; }
}

/// <summary>A model from the provider's catalogue, for the Load models button.</summary>
public sealed record LlmModelInfo(string Id);
