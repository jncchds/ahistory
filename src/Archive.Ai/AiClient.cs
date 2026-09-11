using System.Diagnostics;
using System.Text.Json;
using Archive.Ai.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ai;

/// <summary>
/// Every model call the app makes goes through here, and every one of them is recorded.
/// </summary>
/// <remarks>
/// <para>
/// The provider knows how to talk to an endpoint; this knows what a call cost, what it was for,
/// and what to write down when it fails. Keeping those apart means the statistics cannot be
/// bypassed by a caller that reaches for a provider directly — there is nothing else to reach for.
/// </para>
/// <para>
/// Recording happens after the call, never around it: <see cref="AiInteractions.Record"/> opens
/// its own connection and writes one row, so a model call is never inside a transaction (P1).
/// </para>
/// </remarks>
public sealed class AiClient(
    ILlmProviderFactory factory, AiInteractions interactions, ILogger<AiClient>? logger = null)
{
    private readonly ILlmProviderFactory _factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    private readonly AiInteractions _interactions =
        interactions ?? throw new ArgumentNullException(nameof(interactions));

    private readonly ILogger _log = logger ?? NullLogger<AiClient>.Instance;

    public async Task<LlmCompletion> ChatAsync(
        AiSettings settings,
        LlmChatRequest request,
        AiPurpose purpose,
        AiSubject subject = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);

        var provider = _factory.Create(settings);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var completion = await provider.ChatAsync(request, cancellationToken).ConfigureAwait(false);

            Record(settings, provider, purpose, subject, started, completion: completion, request: request);

            return completion;
        }
        catch (LlmProviderException ex)
        {
            Record(settings, provider, purpose, subject, started, failure: ex, request: request);

            throw;
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        AiSettings settings,
        IReadOnlyList<string> texts,
        AiSubject subject = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(texts);

        var provider = _factory.Create(settings);
        var started = Stopwatch.GetTimestamp();
        var model = settings.EmbeddingModel.Trim();

        try
        {
            var vectors = await provider.EmbedAsync(model, texts, cancellationToken).ConfigureAwait(false);

            Record(settings, provider, AiPurpose.Embed, subject, started, model: model);

            return vectors;
        }
        catch (LlmProviderException ex)
        {
            Record(settings, provider, AiPurpose.Embed, subject, started, failure: ex, model: model);

            throw;
        }
    }

    /// <summary>Speech to text with the configured transcription model.</summary>
    public async Task<string> TranscribeAsync(
        AiSettings settings,
        byte[] audio,
        string fileName,
        AiSubject subject = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(audio);

        var provider = _factory.Create(settings);
        var started = Stopwatch.GetTimestamp();
        var model = settings.TranscriptionModel.Trim();

        try
        {
            var text = await provider.TranscribeAsync(model, audio, fileName, cancellationToken).ConfigureAwait(false);

            Record(settings, provider, AiPurpose.Transcribe, subject, started, model: model);

            return text;
        }
        catch (LlmProviderException ex)
        {
            Record(settings, provider, AiPurpose.Transcribe, subject, started, failure: ex, model: model);

            throw;
        }
    }

    /// <summary>
    /// The catalogue behind the Load models button.
    /// </summary>
    /// <remarks>
    /// Failure returns an empty list rather than throwing: this is a convenience on a settings
    /// page, and an endpoint that serves completions but not <c>/models</c> is common enough that
    /// treating it as an error would be wrong. The caller reports what it got.
    /// </remarks>
    public async Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        AiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var provider = _factory.Create(settings);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var models = await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);

            Record(settings, provider, AiPurpose.ListModels, default, started, model: string.Empty);

            return models;
        }
        catch (LlmProviderException ex)
        {
            Record(settings, provider, AiPurpose.ListModels, default, started, failure: ex, model: string.Empty);

            throw;
        }
    }

    private void Record(
        AiSettings settings,
        ILlmProvider provider,
        AiPurpose purpose,
        AiSubject subject,
        long started,
        LlmCompletion? completion = null,
        LlmProviderException? failure = null,
        LlmChatRequest? request = null,
        string? model = null)
    {
        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        try
        {
            _interactions.Record(new AiInteraction
            {
                Purpose = purpose,
                Provider = provider.Kind,
                // Scheme, host and path only. Some gateways carry a key in the query string, and
                // a statistics table that quietly stored one would be worse than no statistics.
                Endpoint = provider.BaseUrl.GetLeftPart(UriPartial.Path),
                Model = model ?? request?.Model ?? settings.MainModel,
                SubjectKind = subject.Kind,
                SubjectId = subject.Id,
                DurationMs = elapsed,
                PromptTokens = completion?.PromptTokens,
                CompletionTokens = completion?.CompletionTokens,
                TotalTokens = completion?.TotalTokens,
                ToolCallCount = completion?.ToolCalls.Count ?? 0,
                FinishReason = completion?.FinishReason,
                HttpStatus = failure?.HttpStatus,
                Failed = failure is not null,
                FailureKind = failure is null ? null : KindOf(failure),
                // What the endpoint was actually sent and actually said, when the provider has it;
                // the request as modelled here otherwise.
                RequestJson = settings.RecordPromptBodies && request is not null
                    ? completion?.WireRequest ?? failure?.RequestPayload ?? JsonSerializer.Serialize(request)
                    : null,
                ResponseJson = settings.RecordPromptBodies
                    ? failure?.ErrorPayload ?? completion?.WireResponse
                    : null,
            });
        }
        catch (Exception ex)
        {
            // Statistics are not worth failing a run over. A call that succeeded and then could
            // not be written down is still a call that succeeded.
            _log.LogWarning(ex, "An AI interaction could not be recorded.");
        }
    }

    /// <summary>
    /// The kind of a failure, in the handful of words the statistics page groups by.
    /// </summary>
    /// <remarks>
    /// Deliberately not the message. A provider's error text can quote the request that caused it,
    /// which would put correspondence into a table that is meant to be safe to look at (P6).
    /// </remarks>
    private static string KindOf(LlmProviderException failure) => failure switch
    {
        { HttpStatus: { } status } => $"http_{status}",
        { InnerException: HttpRequestException } => "unreachable",
        { InnerException: OperationCanceledException } => "timeout",
        _ => "bad_response",
    };
}

/// <summary>What a call was about, for the statistics row.</summary>
/// <param name="Kind">"session" or "person"; null for a call the settings page made.</param>
public readonly record struct AiSubject(string? Kind, string? Id)
{
    public static AiSubject Session(string id) => new("session", id);

    public static AiSubject Person(string id) => new("person", id);

    public static AiSubject Thread(string id) => new("thread", id);

    public static AiSubject Media(string hash) => new("media", hash);
}
