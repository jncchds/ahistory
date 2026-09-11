using Archive.Ai.Jobs;
using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// The runner, exercised where a wrong answer is free — which is the reason A2 builds it before
/// any job costs a model call.
/// </summary>
public sealed class AiRunnerTests : IDisposable
{
    private const long Noon = 1_700_000_000;
    private const long Hour = 3_600;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private AiState EnabledState()
    {
        var store = new AiSettingsStore(_directory);

        store.Save(new AiSettings
        {
            Enabled = true,
            MainModel = "test-model",
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        });

        return new AiState(store);
    }

    /// <summary>Records what it was asked to do, and can be told to fail.</summary>
    private sealed class Recording : IAiJobHandler
    {
        internal List<string> Handled { get; } = [];

        internal int FailTimes { get; set; }

        internal AiJobOutcome Outcome { get; set; } = AiJobOutcome.Done;

        public AiJobKind Kind => AiJobKind.Segment;

        public bool UsesModel => false;

        public Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
        {
            Handled.Add(job.SubjectId);

            if (FailTimes > 0)
            {
                FailTimes--;

                throw new InvalidOperationException("as asked");
            }

            return Task.FromResult(Outcome);
        }
    }

    [Fact]
    public async Task Draining_runs_every_queued_job_once()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var handler = new Recording();
        using var runner = new AiRunner(jobs, EnabledState(), [handler]);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h", priority: 2);
        jobs.Enqueue(AiJobKind.Segment, "thread", "t2", "h", priority: 1);

        Assert.Equal(2, await runner.DrainAsync());
        Assert.Equal(["t1", "t2"], handler.Handled);
        Assert.Equal(2, jobs.Counts().Done);
        Assert.Equal(0, await runner.DrainAsync());
    }

    /// <summary>
    /// A job that throws is retried, and then left where it can be seen.
    /// </summary>
    [Fact]
    public async Task A_failing_job_does_not_take_the_runner_with_it()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var handler = new Recording { FailTimes = 1 };
        using var runner = new AiRunner(jobs, EnabledState(), [handler]);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");
        jobs.Enqueue(AiJobKind.Segment, "thread", "t2", "h");

        await runner.DrainAsync();

        Assert.Equal(2, jobs.Counts().Done);
        Assert.Equal(0, jobs.Counts().Failed);
        Assert.Equal(3, handler.Handled.Count);
    }

    /// <summary>
    /// A save written by a newer build can carry work this one does not know how to do.
    /// </summary>
    /// <remarks>
    /// Failing it is right — it stays in the queue as a visible failure rather than being deleted,
    /// and a later build finds it.
    /// </remarks>
    [Fact]
    public async Task Work_this_build_cannot_do_fails_rather_than_disappearing()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        using var runner = new AiRunner(jobs, EnabledState(), []);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");

        await runner.DrainAsync();

        Assert.Equal(1, jobs.Counts().Failed);
        Assert.Equal("no_handler", save.Text("SELECT last_error_kind FROM ai_job;"));
    }

    [Fact]
    public async Task A_job_that_wants_reviewing_is_counted_apart_from_one_that_is_done()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        using var runner = new AiRunner(
            jobs, EnabledState(), [new Recording { Outcome = AiJobOutcome.NeedsReview }]);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");

        await runner.DrainAsync();

        Assert.Equal(1, jobs.Counts().NeedsReview);
    }

    /// <summary>
    /// Segmentation end to end: plan the work, drain it, and the archive has sessions.
    /// </summary>
    [Fact]
    public async Task Planning_and_draining_segments_the_whole_archive()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "morning"), (Noon + (9 * Hour), "evening"));
        save.Seed("t2", (Noon, "hello"));

        var jobs = new AiJobs(save.Database);
        var segmenter = new SessionSegmenter(save.Database);
        using var runner = new AiRunner(jobs, EnabledState(), [new SegmentJobHandler(segmenter)]);
        var work = new AiWork(save.Database, jobs, segmenter, runner);

        Assert.Equal(2, work.PlanSegmentation());

        await runner.DrainAsync();

        Assert.Equal(3, save.Count("SELECT count(*) FROM session;"));
        Assert.Equal(2, jobs.Counts().Done);

        // And planning again finds nothing, because nothing changed.
        Assert.Equal(0, work.PlanSegmentation());
    }

    /// <summary>
    /// Pausing cancels what is in flight and marks nothing failed.
    /// </summary>
    /// <remarks>
    /// Stopping is not failing. The lease expires and the next start takes the job again — the
    /// same route a crash takes, which is why pausing exercises crash recovery every time.
    /// </remarks>
    [Fact]
    public async Task Pausing_leaves_the_job_to_be_taken_again()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var started = new TaskCompletionSource();

        using var runner = new AiRunner(jobs, EnabledState(), [new Blocking(started)]);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");

        runner.Start();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runner.PauseAsync();

        Assert.Equal(0, jobs.Counts().Failed);
        Assert.Equal(1, jobs.Counts().Running);

        // Which the next start reclaims, because the app is no longer holding it.
        save.Execute("UPDATE ai_job SET lease_utc = '2000-01-01T00:00:00.0000000Z';");

        Assert.Equal(1, jobs.ReclaimExpired());
    }

    /// <summary>Allowed or not, as told: the stand-in for consent to send text somewhere.</summary>
    private sealed class Gated(bool allowed) : IAiJobHandler
    {
        internal int Calls { get; private set; }

        public AiJobKind Kind => AiJobKind.Extract;

        public bool UsesModel => true;

        public bool IsAllowed => allowed;

        public Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(AiJobOutcome.Done);
        }
    }

    /// <summary>
    /// Queued work that may not be done now is not done — however it came to be queued.
    /// </summary>
    /// <remarks>
    /// Found by running the real app: a save carrying extraction jobs queued earlier was drained on
    /// launch against whatever endpoint was configured then, with no question asked, because
    /// consent was checked when work was planned and not when it ran. A queued job records that
    /// work exists; it is not permission to send anything anywhere.
    /// </remarks>
    [Fact]
    public async Task Work_that_is_not_allowed_waits_untouched_while_other_work_runs()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var gated = new Gated(allowed: false);
        var segment = new Recording();
        using var runner = new AiRunner(jobs, EnabledState(), [gated, segment]);

        jobs.Enqueue(AiJobKind.Extract, "session", "s1", "h", priority: 9);
        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h", priority: 1);

        Assert.Equal(1, await runner.DrainAsync());
        Assert.Equal(0, gated.Calls);
        Assert.Equal(["t1"], segment.Handled);
        Assert.Equal(1, jobs.Counts(AiJobKind.Extract).Pending);
        Assert.Equal(0, save.Count("SELECT attempts FROM ai_job WHERE kind = 'extract';"));
    }

    private AiState StateWithWidth(int width)
    {
        var store = new AiSettingsStore(_directory);

        store.Save(new AiSettings
        {
            Enabled = true,
            MainModel = "test-model",
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
            MaxParallelCalls = width,
        });

        return new AiState(store);
    }

    /// <summary>
    /// Records the most calls it had in flight at once.
    /// </summary>
    /// <remarks>
    /// Each call is held open until <c>until</c> of them are in flight, or until <c>hold</c> runs
    /// out — so a runner that never reaches the width fails the test instead of hanging it.
    /// </remarks>
    private sealed class Concurrent(AiJobKind kind, bool usesModel, int until, TimeSpan hold) : IAiJobHandler
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _full = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;

        internal int Widest { get; private set; }

        public AiJobKind Kind => kind;

        public bool UsesModel => usesModel;

        public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _inFlight++;
                Widest = Math.Max(Widest, _inFlight);

                if (_inFlight >= until)
                {
                    _full.TrySetResult();
                }
            }

            await Task.WhenAny(_full.Task, Task.Delay(hold, cancellationToken));

            lock (_gate)
            {
                _inFlight--;
            }

            return AiJobOutcome.Done;
        }
    }

    /// <summary>
    /// Model calls run as many at once as the setting allows.
    /// </summary>
    /// <remarks>
    /// A call spends nearly all its time waiting on the endpoint. At twenty seconds a session, one
    /// at a time turns a real archive into days of reading.
    /// </remarks>
    [Fact]
    public async Task Model_calls_run_as_many_at_once_as_the_setting_allows()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var handler = new Concurrent(AiJobKind.Extract, usesModel: true, until: 3, TimeSpan.FromSeconds(5));
        using var runner = new AiRunner(jobs, StateWithWidth(3), [handler]);

        for (var i = 0; i < 3; i++)
        {
            jobs.Enqueue(AiJobKind.Extract, "session", $"s{i}", "h");
        }

        Assert.Equal(3, await runner.DrainAsync());
        Assert.Equal(3, handler.Widest);
        Assert.Equal(3, jobs.Counts(AiJobKind.Extract).Done);
    }

    [Fact]
    public async Task One_call_at_a_time_when_the_setting_says_one()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var handler = new Concurrent(AiJobKind.Extract, usesModel: true, until: 2, TimeSpan.FromMilliseconds(50));
        using var runner = new AiRunner(jobs, StateWithWidth(1), [handler]);

        for (var i = 0; i < 3; i++)
        {
            jobs.Enqueue(AiJobKind.Extract, "session", $"s{i}", "h");
        }

        await runner.DrainAsync();

        Assert.Equal(1, handler.Widest);
    }

    /// <summary>Local work never runs side by side, whatever the setting — it only contends for the writer.</summary>
    [Fact]
    public async Task Local_work_runs_one_at_a_time_whatever_the_setting()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var handler = new Concurrent(AiJobKind.Segment, usesModel: false, until: 2, TimeSpan.FromMilliseconds(50));
        using var runner = new AiRunner(jobs, StateWithWidth(4), [handler]);

        for (var i = 0; i < 3; i++)
        {
            jobs.Enqueue(AiJobKind.Segment, "thread", $"t{i}", "h");
        }

        await runner.DrainAsync();

        Assert.Equal(1, handler.Widest);
    }

    /// <summary>A limit counts jobs taken, parallel or not, so a demonstration stays a minute.</summary>
    [Fact]
    public async Task A_limit_holds_when_calls_run_side_by_side()
    {
        using var save = new TempSave();

        var jobs = new AiJobs(save.Database);
        var handler = new Concurrent(AiJobKind.Extract, usesModel: true, until: 99, TimeSpan.FromMilliseconds(20));
        using var runner = new AiRunner(jobs, StateWithWidth(4), [handler]);

        for (var i = 0; i < 6; i++)
        {
            jobs.Enqueue(AiJobKind.Extract, "session", $"s{i}", "h");
        }

        Assert.Equal(2, await runner.DrainAsync(2));
        Assert.Equal(4, jobs.Counts(AiJobKind.Extract).Pending);
    }

    /// <summary>Waits to be cancelled, after saying it has started.</summary>
    private sealed class Blocking(TaskCompletionSource started) : IAiJobHandler
    {
        public AiJobKind Kind => AiJobKind.Segment;

        public bool UsesModel => false;

        public async Task<AiJobOutcome> HandleAsync(AiJob job, CancellationToken cancellationToken)
        {
            started.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken);

            return AiJobOutcome.Done;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
