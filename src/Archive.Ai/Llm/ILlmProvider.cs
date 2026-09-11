namespace Archive.Ai.Llm;

/// <summary>
/// One implementation per provider family. Nothing above this line knows an HTTP shape.
/// </summary>
public interface ILlmProvider
{
    LlmProviderKind Kind { get; }

    /// <summary>The base URL actually in use, defaults resolved. Shown in the UI and recorded in stats.</summary>
    Uri BaseUrl { get; }

    Task<LlmCompletion> ChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Text to vectors, one per text, in the order given.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        string model, IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    /// <summary>Speech to text, through <c>/audio/transcriptions</c>.</summary>
    Task<string> TranscribeAsync(
        string model, byte[] audio, string fileName, CancellationToken cancellationToken = default);

    /// <summary>The catalogue behind the Load models button. Suggestions, never a validator.</summary>
    Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds a provider from settings. Nothing constructs a provider directly.</summary>
public interface ILlmProviderFactory
{
    ILlmProvider Create(AiSettings settings);
}

/// <summary>
/// A call failed. Carries what the stats row needs so the caller does not have to unpick an
/// HTTP exception to record it.
/// </summary>
public sealed class LlmProviderException : Exception
{
    public LlmProviderException(
        string message, int? httpStatus = null, string? errorPayload = null, Exception? inner = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
        ErrorPayload = errorPayload;
    }

    public LlmProviderException()
    {
    }

    public LlmProviderException(string message)
        : base(message)
    {
    }

    public LlmProviderException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public int? HttpStatus { get; }

    /// <summary>
    /// The provider's own error body.
    /// </summary>
    /// <remarks>
    /// Shown to the user and recorded as a failure reason, because "Provider returned 400" without
    /// the sentence that came with it is the single most useless error message in this class of
    /// app. It is never logged (P6) — an error body can quote the request.
    /// </remarks>
    public string? ErrorPayload { get; }

    /// <summary>
    /// The request that failed, as it went on the wire — for prompt recording, which is where a
    /// failing prompt most needs to be seen.
    /// </summary>
    public string? RequestPayload { get; internal set; }
}
