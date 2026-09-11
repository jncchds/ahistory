using Archive.Ai.Llm;
using Archive.Ai.Search;

namespace Archive.Ai.Jobs;

/// <summary>
/// Embeds one thread's sessions with the configured model, in batches (A6).
/// </summary>
/// <remarks>
/// <para>
/// Per thread rather than per session: a real archive has tens of thousands of sessions, and a job
/// and a call for each would spend more on bookkeeping than on vectors. One job per thread, one call
/// per batch, and a transaction per batch, so a thread interrupted halfway keeps what it finished.
/// </para>
/// <para>
/// Sending a session's text to be embedded sends it as surely as reading it does, so it waits for
/// the same agreement, and skips exactly what extraction skips.
/// </para>
/// </remarks>
public sealed class EmbedJobHandler(
    EmbeddingStore store,
    SessionText text,
    AiClient client,
    AiState state,
    ILlmProviderFactory factory) : IAiJobHandler
{
    /// <summary>Sessions per call. Small enough for a local server, large enough to matter.</summary>
    private const int BatchSize = 16;

    private readonly EmbeddingStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly SessionText _text = text ?? throw new ArgumentNullException(nameof(text));

    private readonly AiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public AiJobKind Kind => AiJobKind.Embed;

    public bool UsesModel => true;

    public bool IsAllowed =>
        !string.IsNullOrWhiteSpace(_state.Current.EmbeddingModel)
        && AiConsent.CoversExtraction(_state.Current, _factory);

    public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var settings = _state.Current;
        var model = settings.EmbeddingModel.Trim();

        var pending = _store.Pending(job.SubjectId, model);

        foreach (var batch in pending.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var texts = batch
                .Select(p => (p.SessionId, p.InputHash, Text: _text.For(p.SessionId, model)))
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .ToList();

            if (texts.Count == 0)
            {
                continue;
            }

            var vectors = await _client
                .EmbedAsync(settings, [.. texts.Select(t => t.Text!)], AiSubject.Thread(job.SubjectId), cancellationToken)
                .ConfigureAwait(false);

            if (vectors.Count != texts.Count)
            {
                // A provider that returned fewer vectors than it was sent texts has made it
                // impossible to know which is which. Failing is the only answer that cannot attach
                // a vector to the wrong conversation.
                throw new InvalidOperationException("The endpoint returned a different number of vectors than it was sent.");
            }

            _store.Write(model, [.. texts.Select((t, i) => (t.SessionId, t.InputHash, vectors[i]))]);
        }

        return AiJobOutcome.Done;
    }
}
