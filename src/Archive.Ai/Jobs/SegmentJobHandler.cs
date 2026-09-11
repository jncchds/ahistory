using Archive.Ai.Sessions;

namespace Archive.Ai.Jobs;

/// <summary>Turns one thread into sessions. The only job kind that involves no model at all.</summary>
public sealed class SegmentJobHandler(SessionSegmenter segmenter) : IAiJobHandler
{
    private readonly SessionSegmenter _segmenter =
        segmenter ?? throw new ArgumentNullException(nameof(segmenter));

    public AiJobKind Kind => AiJobKind.Segment;

    public bool UsesModel => false;

    public Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Synchronous work on a background thread rather than fake-async: it is SQLite reads and
        // writes, and wrapping them in Task.Run at least keeps the caller's thread free.
        return Task.Run(
            () =>
            {
                _segmenter.SegmentThread(job.SubjectId, cancellationToken);

                return AiJobOutcome.Done;
            },
            cancellationToken);
    }
}
