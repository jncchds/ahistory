using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// §7: "processed" is not a boolean, it is a boolean at a version — and a page that says "done"
/// without saying at what is lying by omission.
/// </summary>
public sealed class AiCoverageTests
{
    private const long Noon = 1_700_000_000;
    private const long Hour = 3_600;

    private static string Long(int n) =>
        string.Join(' ', Enumerable.Range(0, n).Select(i => $"word{i}"));

    [Fact]
    public void An_archive_nothing_has_read_reports_nothing_read()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "hello"));

        var coverage = new AiCoverage(save.Database).Summary();

        Assert.Equal(1, coverage.Threads);
        Assert.Equal(0, coverage.ThreadsSegmented);
        Assert.Equal(1, coverage.Messages);
        Assert.Equal(0, coverage.MessagesInSessions);
        Assert.False(coverage.IsSegmented);
        Assert.Equal(0, coverage.SegmentedFraction);
    }

    [Fact]
    public void Coverage_follows_the_segmenter_through_the_archive()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, Long(30)), (Noon + (9 * Hour), "ok"));
        save.Seed("t2", (Noon, "hi"));

        var segmenter = new SessionSegmenter(save.Database);
        var coverage = new AiCoverage(save.Database);

        segmenter.SegmentThread("t1");

        var half = coverage.Summary();

        Assert.Equal(2, half.Threads);
        Assert.Equal(1, half.ThreadsSegmented);
        Assert.False(half.IsSegmented);
        Assert.Equal(0.5, half.SegmentedFraction);

        segmenter.SegmentThread("t2");

        var all = coverage.Summary();

        Assert.True(all.IsSegmented);
        Assert.Equal(3, all.Sessions);
        Assert.Equal(3, all.Messages);
        Assert.Equal(3, all.MessagesInSessions);

        // One of the three carries something; the other two are "ok" and "hi".
        Assert.Equal(1, all.Substantive);
    }

    /// <summary>
    /// A segmenter version that no longer matches is not coverage.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is a page reporting an archive as fully read when the rules that
    /// read it have since changed — the exact thing §7 means by "processed at a version".
    /// </remarks>
    [Fact]
    public void Work_done_under_other_rules_does_not_count()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "hello"));
        new SessionSegmenter(save.Database).SegmentThread("t1");

        Assert.True(new AiCoverage(save.Database).Summary().IsSegmented);

        save.Execute("UPDATE session SET segmenter_version = '0';");

        var coverage = new AiCoverage(save.Database).Summary();

        Assert.Equal(0, coverage.ThreadsSegmented);
        Assert.Equal(0, coverage.Sessions);
        Assert.False(coverage.IsSegmented);
    }

    /// <summary>
    /// A person's coverage is their threads' coverage, because sessions belong to threads.
    /// </summary>
    [Fact]
    public void A_person_is_covered_as_far_as_their_conversations_are()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "hello"));
        save.Seed("t2", (Noon, "hello again"));

        var coverage = new AiCoverage(save.Database);

        Assert.Equal(2, coverage.ForPerson("p_them").Threads);
        Assert.Equal(0, coverage.ForPerson("p_them").ThreadsSegmented);

        new SessionSegmenter(save.Database).SegmentThread("t1");

        Assert.Equal(1, coverage.ForPerson("p_them").ThreadsSegmented);
        Assert.Equal(0, coverage.ForPerson("nobody").Threads);
    }
}
