using Archive.Ai.Llm;
using Archive.Ai.Merging;

namespace Archive.Ai.Jobs;

/// <summary>Merges one person's restated facts, and closes values that were replaced (A4).</summary>
/// <remarks>
/// A model call, so it waits for the same agreement extraction does: what it sends is claims read
/// out of the person's messages, and those are theirs as much as the messages were.
/// </remarks>
public sealed class AdjudicateJobHandler(FactMerger merger, AiState state, ILlmProviderFactory factory)
    : IAiJobHandler
{
    private readonly FactMerger _merger = merger ?? throw new ArgumentNullException(nameof(merger));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public AiJobKind Kind => AiJobKind.Adjudicate;

    public bool UsesModel => true;

    public bool IsAllowed => AiConsent.CoversExtraction(_state.Current, _factory);

    public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var report = await _merger.RunAsync(_state.Current, job.SubjectId, cancellationToken).ConfigureAwait(false);

        return report.NeedsReview ? AiJobOutcome.NeedsReview : AiJobOutcome.Done;
    }
}
