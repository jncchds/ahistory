using Archive.Ai.Jobs;

namespace Archive.Ai.Tests;

/// <summary>
/// The queue is what makes enrichment pausable, resumable and survivable, so these are mostly
/// about the states nobody wants to be in.
/// </summary>
public sealed class AiJobsTests
{
    private static AiJobs Queue(TempSave save) => new(save.Database);

    [Fact]
    public void Asking_for_the_same_work_twice_queues_it_once()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        Assert.True(jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "hash-1"));
        Assert.True(jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "hash-1"));

        Assert.Equal(1, save.Count("SELECT count(*) FROM ai_job;"));
        Assert.Equal(1, jobs.Counts().Pending);
    }

    /// <summary>
    /// Work already done stays done until its inputs change.
    /// </summary>
    /// <remarks>
    /// This is the whole of §12: re-checking the archive on every start has to be free, and a
    /// re-import that changed nothing must dirty nothing. Without it, opening the app would
    /// re-read a decade.
    /// </remarks>
    [Fact]
    public void Finished_work_is_not_queued_again_unless_its_inputs_changed()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "hash-1");
        jobs.Complete(jobs.Claim()!.Id);

        Assert.False(jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "hash-1"));
        Assert.Equal(1, jobs.Counts().Done);

        Assert.True(jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "hash-2"));
        Assert.Equal(1, jobs.Counts().Pending);
        Assert.Equal(1, save.Count("SELECT count(*) FROM ai_job;"));
    }

    [Fact]
    public void The_highest_priority_job_is_claimed_first()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Segment, "thread", "old", "h", priority: 1);
        jobs.Enqueue(AiJobKind.Segment, "thread", "new", "h", priority: 9);
        jobs.Enqueue(AiJobKind.Segment, "thread", "middle", "h", priority: 5);

        Assert.Equal("new", jobs.Claim()!.SubjectId);
        Assert.Equal("middle", jobs.Claim()!.SubjectId);
        Assert.Equal("old", jobs.Claim()!.SubjectId);
        Assert.Null(jobs.Claim());
    }

    /// <summary>A claimed job is not claimable again, which is what stops two runners doubling up.</summary>
    [Fact]
    public void Claiming_takes_the_job_out_of_the_queue()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");

        Assert.NotNull(jobs.Claim());
        Assert.Null(jobs.Claim());
        Assert.Equal(1, jobs.Counts().Running);
    }

    /// <summary>
    /// A crash mid-job is a lease that expires, not a job stuck forever.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is the quiet one: every job the app was working on when it closed
    /// stays 'running', the queue stops making progress, and a coverage line reports the archive
    /// as finished when it was abandoned.
    /// </remarks>
    [Fact]
    public void A_job_whose_lease_expired_comes_back()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");
        var claimed = jobs.Claim()!;

        Assert.Equal(0, jobs.ReclaimExpired());

        // As if the app died holding it.
        save.Execute($"UPDATE ai_job SET lease_utc = '2000-01-01T00:00:00.0000000Z' WHERE id = {claimed.Id};");

        Assert.Equal(1, jobs.ReclaimExpired());
        Assert.Equal(1, jobs.Counts().Pending);
        Assert.NotNull(jobs.Claim());
    }

    [Fact]
    public void A_job_is_retried_and_then_left_visibly_failed()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");

        for (var attempt = 1; attempt < AiJobs.MaxAttempts; attempt++)
        {
            var job = jobs.Claim()!;
            jobs.Fail(job.Id, "timeout", job.Attempts);

            Assert.Equal(1, jobs.Counts().Pending);
        }

        var last = jobs.Claim()!;
        jobs.Fail(last.Id, "timeout", last.Attempts);

        Assert.Equal(1, jobs.Counts().Failed);
        Assert.Null(jobs.Claim());
        Assert.Equal("timeout", save.Text("SELECT last_error_kind FROM ai_job;"));
    }

    [Fact]
    public void A_job_can_finish_and_still_want_looking_at()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");
        jobs.Complete(jobs.Claim()!.Id, needsReview: true);

        Assert.Equal(1, jobs.Counts().NeedsReview);
        Assert.Equal(0, jobs.Counts().Done);
    }

    [Fact]
    public void Counts_can_be_narrowed_to_one_kind()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h");
        jobs.Enqueue(AiJobKind.Embed, "session", "s1", "h");

        Assert.Equal(2, jobs.Counts().Total);
        Assert.Equal(1, jobs.Counts(AiJobKind.Segment).Total);
    }

    /// <summary>
    /// Work that may not be done now is skipped, and left exactly as it was.
    /// </summary>
    [Fact]
    public void A_withheld_kind_is_not_claimed_and_not_touched()
    {
        using var save = new TempSave();
        var jobs = Queue(save);

        jobs.Enqueue(AiJobKind.Extract, "session", "s1", "h", priority: 9);
        jobs.Enqueue(AiJobKind.Segment, "thread", "t1", "h", priority: 1);

        Assert.Equal(AiJobKind.Segment, jobs.Claim([AiJobKind.Extract])!.Kind);
        Assert.Null(jobs.Claim([AiJobKind.Extract]));

        Assert.Equal(0, save.Count("SELECT attempts FROM ai_job WHERE kind = 'extract';"));
        Assert.Equal("pending", save.Text("SELECT state FROM ai_job WHERE kind = 'extract';"));

        // And the moment nothing is withheld, it is simply next.
        Assert.Equal(AiJobKind.Extract, jobs.Claim()!.Kind);
    }
}
