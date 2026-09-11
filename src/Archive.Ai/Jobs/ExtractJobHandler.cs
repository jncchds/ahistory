using Archive.Ai.Extraction;
using Archive.Ai.Llm;

namespace Archive.Ai.Jobs;

/// <summary>Reads one session with a model and records what it says.</summary>
/// <remarks>
/// The first job kind that costs anything, which is why it is also the first that can come back
/// <see cref="AiJobOutcome.NeedsReview"/>: a session the model could not produce a usable call for
/// is a visible failure rather than a gap in a coverage number that still reads 100%.
/// </remarks>
public sealed class ExtractJobHandler(ExtractRunner runner, AiState state, ILlmProviderFactory factory)
    : IAiJobHandler
{
    private readonly ExtractRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public AiJobKind Kind => AiJobKind.Extract;

    public bool UsesModel => true;

    /// <summary>
    /// Only for an endpoint the user has agreed to send text to, as the settings stand right now.
    /// </summary>
    /// <remarks>
    /// Checked when a job would run, not when it was queued. The first version checked only when
    /// planning, and running the real app showed what that allows: a save carrying extraction jobs
    /// queued earlier was drained on launch against the endpoint configured at that moment, with
    /// no question asked. Agree for a local model, queue a thousand sessions, point the settings at
    /// a hosted API and restart — and the queue would have gone there. A queued job records that
    /// work exists; it is not permission to send anything anywhere.
    /// </remarks>
    public bool IsAllowed => AiConsent.CoversExtraction(_state.Current, _factory);

    public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var settings = _state.Current;

        if (!AiConsent.CoversExtraction(settings, _factory))
        {
            // Reachable only if consent went stale between the claim and this line. Not a failure:
            // the work is simply not to be done yet, and the job is left for later.
            throw new OperationCanceledException("Sending text to this endpoint has not been agreed to.");
        }

        var report = await _runner
            .RunAsync(settings, job.SubjectId, job.InputHash ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return report.Outcome == ExtractionOutcome.NeedsReview
            ? AiJobOutcome.NeedsReview
            : AiJobOutcome.Done;
    }
}
