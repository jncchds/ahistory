using Archive.Ai.Attachments;
using Archive.Ai.Llm;

namespace Archive.Ai.Jobs;

/// <summary>Copies the text out of one image (A7).</summary>
/// <remarks>
/// Only with a vision model configured, and only for an endpoint agreed to: a screenshot is often
/// someone's private conversation somewhere else, and sending it is sending that.
/// </remarks>
public sealed class OcrJobHandler(MediaReader reader, AiState state, ILlmProviderFactory factory) : IAiJobHandler
{
    private readonly MediaReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public AiJobKind Kind => AiJobKind.Ocr;

    public bool UsesModel => true;

    public bool IsAllowed =>
        !string.IsNullOrWhiteSpace(_state.Current.VisionModel)
        && AiConsent.CoversExtraction(_state.Current, _factory);

    public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await _reader.OcrAsync(_state.Current, job.SubjectId, cancellationToken).ConfigureAwait(false);

        return AiJobOutcome.Done;
    }
}

/// <summary>Transcribes one voice or video message (A7).</summary>
public sealed class TranscribeJobHandler(MediaReader reader, AiState state, ILlmProviderFactory factory) : IAiJobHandler
{
    private readonly MediaReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public AiJobKind Kind => AiJobKind.Transcribe;

    public bool UsesModel => true;

    public bool IsAllowed =>
        !string.IsNullOrWhiteSpace(_state.Current.TranscriptionModel)
        && AiConsent.CoversExtraction(_state.Current, _factory);

    public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await _reader.TranscribeAsync(_state.Current, job.SubjectId, cancellationToken).ConfigureAwait(false);

        return AiJobOutcome.Done;
    }
}
