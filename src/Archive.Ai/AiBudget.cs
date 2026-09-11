namespace Archive.Ai;

/// <summary>
/// How much of the daily token budget has gone, and whether model work may carry on.
/// </summary>
/// <remarks>
/// <para>
/// Measured from the calls recorded in the save rather than counted in memory, so that closing and
/// reopening the app does not hand out a fresh allowance — the runner starts with every launch,
/// and a cap that reset with it would protect nothing on a paid endpoint.
/// </para>
/// <para>
/// Enforced where consent is: a kind of work that may not run is withheld at the moment a job would
/// be claimed. Nothing is failed and no attempt is spent; the work simply waits, and resumes by
/// itself as the day moves on.
/// </para>
/// </remarks>
public sealed class AiBudget(AiInteractions interactions)
{
    /// <summary>The window the budget is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    private readonly AiInteractions _interactions =
        interactions ?? throw new ArgumentNullException(nameof(interactions));

    /// <summary>Tokens spent on model calls in the last <see cref="Window"/>.</summary>
    public long Spent() => _interactions.TokensSince(DateTime.UtcNow - Window);

    /// <summary>True when there is a cap and the last day's calls have reached it.</summary>
    public bool IsReached(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.DailyTokenBudget > 0 && Spent() >= settings.DailyTokenBudget;
    }
}
