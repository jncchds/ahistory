using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ai.Jobs;

/// <summary>What a handler made of a job.</summary>
public enum AiJobOutcome
{
    Done,

    /// <summary>Finished, but a person should look at it. Never a silent discard (§5.4).</summary>
    NeedsReview,
}

/// <summary>One kind of work the runner knows how to do.</summary>
public interface IAiJobHandler
{
    AiJobKind Kind { get; }

    /// <summary>
    /// Whether this work involves a model call.
    /// </summary>
    /// <remarks>
    /// Decides both how many may run at once and whether it costs anything. Segmentation is local
    /// and write-heavy, so running several at once only makes them contend for SQLite's one
    /// writer; model calls are the opposite, and are what the parallelism setting is for.
    /// </remarks>
    bool UsesModel { get; }

    /// <summary>
    /// Whether this kind of work may be done right now.
    /// </summary>
    /// <remarks>
    /// Asked every time a job would be claimed, never once when it was queued. A queued job is only
    /// a record that the work exists; whether it may be done depends on the settings as they stand
    /// when it would run. Work that is not allowed is left exactly where it is — not claimed, not
    /// attempted, not failed — and the rest of the queue carries on around it.
    /// </remarks>
    bool IsAllowed => true;

    Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Drains the queue in the background for as long as AI is enabled.
/// </summary>
/// <remarks>
/// <para>
/// There is no indexing mode and no first-run wizard: work arrives in <c>ai_job</c> by
/// invalidation, and this empties it. The only controls are start, pause, and how fast
/// (ai-plan.md §11.1).
/// </para>
/// <para>
/// It lives and dies with the app. No daemon, no scheduled task, and nothing that reads the
/// archive while the app is closed — which is a promise about a machine, not only about a feature.
/// </para>
/// <para>
/// Everything it does is interruptible. Pausing cancels in flight, the job's lease expires, and
/// the next start picks it up; that is the same path a crash takes, so the recovery route is
/// exercised every time somebody presses pause rather than only after something goes wrong.
/// </para>
/// </remarks>
public sealed class AiRunner : IDisposable
{
    /// <summary>How long the runner sleeps when the queue is empty before looking again.</summary>
    /// <remarks>
    /// Also how often expired leases are reclaimed. Long enough to be free, short enough that work
    /// enqueued by an import that finished while nobody was looking starts on its own.
    /// </remarks>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(20);

    private readonly AiJobs _jobs;
    private readonly AiState _state;
    private readonly Dictionary<AiJobKind, IAiJobHandler> _handlers;
    private readonly ILogger _log;
    private readonly AiBudget? _budget;
    private readonly SemaphoreSlim _wake = new(0);

    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;

    /// <param name="budget">
    /// The daily token cap. Without one, nothing is ever held for cost — which is what a test that
    /// is not about the budget wants, and what the head never does.
    /// </param>
    public AiRunner(
        AiJobs jobs,
        AiState state,
        IEnumerable<IAiJobHandler> handlers,
        ILogger<AiRunner>? logger = null,
        AiBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _handlers = handlers.ToDictionary(h => h.Kind);
        _log = logger ?? NullLogger<AiRunner>.Instance;
        _budget = budget;

        // Switching AI off stops the work, not just the pages.
        _state.Changed += (_, _) =>
        {
            if (_state.Current.Enabled)
            {
                Start();
            }
            else
            {
                _ = PauseAsync();
            }
        };
    }

    /// <summary>Raised after every job, so a progress line can follow along.</summary>
    public event EventHandler? Progressed;

    /// <summary>
    /// Asked, after a pass that did some work, whether that work made more; returns how much.
    /// </summary>
    /// <remarks>
    /// Set by the head, which is the only place that knows both the planner and the consent rules.
    /// The runner itself neither plans work nor decides what may be sent where.
    /// </remarks>
    public Func<int>? Replan { get; set; }

    public bool IsRunning => !_loop.IsCompleted;

    /// <summary>True while the daily token budget is holding model work.</summary>
    public bool IsOverBudget => _budget?.IsReached(_state.Current) ?? false;

    /// <summary>Begins draining, if it is not already.</summary>
    public void Start()
    {
        if (IsRunning || !_state.Current.Enabled)
        {
            return;
        }

        _stopping = new CancellationTokenSource();
        _loop = LoopAsync(_stopping.Token);
    }

    /// <summary>Stops after cancelling whatever is in flight.</summary>
    public async Task PauseAsync()
    {
        if (_stopping is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Asked for.
        }

        _stopping.Dispose();
        _stopping = null;
    }

    /// <summary>Tells the runner there is work, without waiting for the idle timer.</summary>
    public void Poke()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>
    /// Runs the queue to empty and returns.
    /// </summary>
    /// <remarks>
    /// What the CLI and the tests use. The same code the background loop runs, so a command-line
    /// drain and a drain nobody is watching cannot behave differently.
    /// </remarks>
    public Task<int> DrainAsync(CancellationToken cancellationToken = default) =>
        DrainAsync(int.MaxValue, cancellationToken);

    /// <summary>
    /// Runs at most <paramref name="limit"/> jobs and returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stopping early is free: what was done stays done and the rest stays queued, because that is
    /// what a queue is. The limit exists so a demonstration is a minute rather than an evening.
    /// </para>
    /// <para>
    /// Model calls run up to <see cref="AiSettings.MaxParallelCalls"/> at once, because a call spends
    /// almost all of its time waiting on the endpoint; one at a time, a real archive is days of
    /// reading at twenty seconds a session. Local work runs one at a time: it is write-heavy, and
    /// several at once only contend for SQLite's single writer.
    /// </para>
    /// </remarks>
    public async Task<int> DrainAsync(int limit, CancellationToken cancellationToken = default)
    {
        _jobs.ReclaimExpired();

        var handled = 0;
        var inFlight = new List<Task>();

        try
        {
            while (handled < limit && !cancellationToken.IsCancellationRequested)
            {
                // Read every time round: a user who turns the setting down because their machine is
                // struggling means now, not after the current drain.
                var width = Math.Max(1, _state.Current.MaxParallelCalls);

                if (inFlight.Count < width && _jobs.Claim(Withheld()) is { } job)
                {
                    handled++;

                    if (UsesModel(job))
                    {
                        inFlight.Add(RunOneAsync(job, cancellationToken));
                    }
                    else
                    {
                        await RunOneAsync(job, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (inFlight.Count == 0)
                {
                    break;
                }

                var finished = await Task.WhenAny(inFlight).ConfigureAwait(false);
                inFlight.Remove(finished);

                await finished.ConfigureAwait(false);
            }
        }
        finally
        {
            // A drain that returns has nothing still running behind it. On a pause the calls in
            // flight are cancelled, and this waits for them to notice.
            if (inFlight.Count > 0)
            {
                await Task.WhenAll(inFlight).ConfigureAwait(false);
            }
        }

        return handled;
    }

    private bool UsesModel(AiJob job) =>
        _handlers.TryGetValue(job.Kind, out var handler) && handler.UsesModel;

    /// <summary>
    /// Kinds of work that exist but may not be done now, and so are not to be claimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recomputed for every claim: consent can be withdrawn, or made stale by pointing the settings
    /// at a different endpoint, halfway through a drain — and the very next job has to see that.
    /// </para>
    /// <para>
    /// The daily budget works the same way. Once it is spent, every kind of work that calls a model
    /// is held and local work carries on; the idle timer looks again, and the work resumes as the
    /// oldest of the day's calls fall out of the window.
    /// </para>
    /// </remarks>
    private AiJobKind[] Withheld()
    {
        var overBudget = IsOverBudget;

        return [.. _handlers.Values
            .Where(handler => !handler.IsAllowed || (overBudget && handler.UsesModel))
            .Select(handler => handler.Kind)];
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        // Yield first, so Start() returns before any work begins and IsRunning is true by the
        // time the caller looks at it.
        await Task.Yield();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var handled = await DrainAsync(cancellationToken).ConfigureAwait(false);

                // Work done can make more work: a segmented thread has sessions to read. Asking
                // once a pass ends, rather than from inside a handler, keeps handlers from having to
                // know the planner — which itself needs the runner — exists.
                if (handled > 0 && (Replan?.Invoke() ?? 0) > 0)
                {
                    continue;
                }

                if (handled == 0)
                {
                    await _wake.WaitAsync(Idle, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop outliving one bad job is the whole point of having a queue. Anything
                // that reaches here is a bug rather than a failed job, so it is logged and the
                // runner waits instead of spinning on it.
                _log.LogError(ex, "The AI runner hit an unexpected failure and is backing off.");

                try
                {
                    await Task.Delay(Idle, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RunOneAsync(AiJob job, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(job.Kind, out var handler))
        {
            // A job for work this build cannot do — a save written by a newer version. Failing it
            // is right: it is not lost, and a future build will find it.
            _jobs.Fail(job.Id, "no_handler", AiJobs.MaxAttempts);

            return;
        }

        try
        {
            var outcome = await handler.HandleAsync(job, cancellationToken).ConfigureAwait(false);

            _jobs.Complete(job.Id, outcome == AiJobOutcome.NeedsReview);
        }
        catch (OperationCanceledException)
        {
            // Paused. The lease expires and the next run takes it again; nothing is marked failed,
            // because stopping is not failing.
            throw;
        }
        catch (Exception ex)
        {
            // The kind, never the message (P6): a failure text can quote what was being processed.
            _jobs.Fail(job.Id, ex.GetType().Name, job.Attempts);

            _log.LogWarning(
                ex, "Job {JobId} ({Kind}) failed on attempt {Attempt}.",
                job.Id, job.Kind, job.Attempts);
        }
        finally
        {
            Progressed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _stopping?.Dispose();
        _wake.Dispose();
    }
}
