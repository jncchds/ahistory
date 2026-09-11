using System.Globalization;
using Archive.Ai.Diary;
using Archive.Ai.Llm;

namespace Archive.Ai.Jobs;

/// <summary>Writes one month of one person's diary (A5).</summary>
/// <remarks>The subject is <c>person|yyyy-MM</c>: one job per window, which is spec §8's unit.</remarks>
public sealed class DiaryJobHandler(DiaryRunner runner, AiState state, ILlmProviderFactory factory) : IAiJobHandler
{
    private readonly DiaryRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public AiJobKind Kind => AiJobKind.Diary;

    public bool UsesModel => true;

    public bool IsAllowed => AiConsent.CoversExtraction(_state.Current, _factory);

    public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var bar = job.SubjectId.LastIndexOf('|');
        var personId = job.SubjectId[..bar];
        var month = DateTime.ParseExact(job.SubjectId[(bar + 1)..], "yyyy-MM", CultureInfo.InvariantCulture);

        var outcome = await _runner
            .MonthAsync(_state.Current, personId, month.Year, month.Month, job.InputHash ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return outcome == DiaryOutcome.NeedsReview ? AiJobOutcome.NeedsReview : AiJobOutcome.Done;
    }
}

/// <summary>Writes a year summary from its months, or a person's profile from their years (A5).</summary>
/// <remarks>The subject is <c>person|yyyy</c> for a year, and the bare person id for a profile.</remarks>
public sealed class RollupJobHandler(DiaryRunner runner, AiState state, ILlmProviderFactory factory) : IAiJobHandler
{
    private readonly DiaryRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public AiJobKind Kind => AiJobKind.Rollup;

    public bool UsesModel => true;

    public bool IsAllowed => AiConsent.CoversExtraction(_state.Current, _factory);

    public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var hash = job.InputHash ?? string.Empty;
        var bar = job.SubjectId.LastIndexOf('|');

        var outcome = bar < 0
            ? await _runner.ProfileAsync(_state.Current, job.SubjectId, hash, cancellationToken).ConfigureAwait(false)
            : await _runner.YearAsync(
                _state.Current,
                job.SubjectId[..bar],
                int.Parse(job.SubjectId[(bar + 1)..], CultureInfo.InvariantCulture),
                hash,
                cancellationToken).ConfigureAwait(false);

        return outcome == DiaryOutcome.NeedsReview ? AiJobOutcome.NeedsReview : AiJobOutcome.Done;
    }
}
