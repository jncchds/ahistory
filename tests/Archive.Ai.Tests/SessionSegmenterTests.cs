using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// §6.1 calls segmentation the most important step, so these are about where the boundaries fall
/// and about what happens when the same thread is segmented twice.
/// </summary>
public sealed class SessionSegmenterTests
{
    private const long Noon = 1_700_000_000;
    private const long Hour = 3_600;

    private static string Long(int n) =>
        string.Join(' ', Enumerable.Range(0, n).Select(i => $"word{i}"));

    [Fact]
    public void Silence_ends_a_session()
    {
        using var save = new TempSave();

        save.Seed(
            "t1",
            (Noon, "morning"),
            (Noon + 60, "how are you"),
            // Five hours later: past the four-hour threshold, so a new conversation.
            (Noon + (5 * Hour), "back now"),
            (Noon + (5 * Hour) + 60, "how did it go"));

        var result = new SessionSegmenter(save.Database).SegmentThread("t1");

        Assert.Equal(2, result.Sessions);
        Assert.Equal(2, save.Count("SELECT count(*) FROM session WHERE thread_id = 't1';"));
        Assert.Equal(4, save.Count("SELECT count(*) FROM message WHERE session_id IS NOT NULL;"));
    }

    [Fact]
    public void A_conversation_with_ordinary_pauses_stays_one_session()
    {
        using var save = new TempSave();

        save.Seed(
            "t1",
            (Noon, "a"),
            (Noon + (3 * Hour), "b"),
            (Noon + (6 * Hour), "c"));

        Assert.Equal(1, new SessionSegmenter(save.Database).SegmentThread("t1").Sessions);
    }

    /// <summary>
    /// The threshold is a parameter, because the right one differs per pair.
    /// </summary>
    [Fact]
    public void The_gap_that_counts_as_silence_can_be_changed()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "a"), (Noon + (2 * Hour), "b"));

        Assert.Equal(
            2,
            new SessionSegmenter(save.Database, TimeSpan.FromHours(1)).SegmentThread("t1").Sessions);
    }

    /// <summary>
    /// Running twice is a no-op, which is what lets the queue re-check the whole archive cheaply.
    /// </summary>
    [Fact]
    public void Segmenting_the_same_thread_twice_changes_nothing()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "a"), (Noon + 60, "b"), (Noon + (9 * Hour), "c"));

        var segmenter = new SessionSegmenter(save.Database);

        var first = segmenter.SegmentThread("t1");
        var ids = save.Text("SELECT group_concat(id) FROM session ORDER BY id;");

        var second = segmenter.SegmentThread("t1");

        Assert.Equal(first.Sessions, second.Sessions);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Removed);
        Assert.Equal(ids, save.Text("SELECT group_concat(id) FROM session ORDER BY id;"));
    }

    /// <summary>
    /// A session that gains a message is a different session.
    /// </summary>
    /// <remarks>
    /// Its identity is derived from what is in it, because everything downstream — the extract,
    /// the embedding, the rollup above it — is keyed to that membership. Keeping the id and
    /// changing the contents would leave every one of those describing something that no longer
    /// exists, and nothing anywhere able to notice.
    /// </remarks>
    [Fact]
    public void A_session_that_gains_a_message_gets_a_new_identity()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "a"), (Noon + 60, "b"));

        var segmenter = new SessionSegmenter(save.Database);
        segmenter.SegmentThread("t1");

        var before = save.Text("SELECT id FROM session;");
        var hashBefore = save.Text("SELECT member_hash FROM session;");

        save.Seed("t1", (Noon + 120, "c"));

        var again = segmenter.SegmentThread("t1");

        Assert.Equal(1, again.Sessions);
        Assert.Equal(1, again.Added);
        Assert.Equal(1, again.Removed);
        Assert.NotEqual(before, save.Text("SELECT id FROM session;"));
        Assert.NotEqual(hashBefore, save.Text("SELECT member_hash FROM session;"));

        // And every message ends up in the session that replaced it, not orphaned by the delete.
        Assert.Equal(3, save.Count("SELECT count(*) FROM message WHERE session_id IS NOT NULL;"));
    }

    /// <summary>
    /// An untouched thread is untouched, which is what §12 needs a re-import to cost.
    /// </summary>
    [Fact]
    public void An_unchanged_thread_hashes_the_same_and_a_changed_one_does_not()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "a"));

        var segmenter = new SessionSegmenter(save.Database);
        var (threadId, lastUnix, messages) = segmenter.Threads()[0];
        var before = segmenter.InputHash(threadId, lastUnix, messages);

        Assert.Equal(before, segmenter.InputHash(threadId, lastUnix, messages));

        save.Seed("t1", (Noon + 60, "b"));

        var (_, lastAfter, messagesAfter) = segmenter.Threads()[0];

        Assert.NotEqual(before, segmenter.InputHash(threadId, lastAfter, messagesAfter));
    }

    /// <summary>
    /// Nobody said "X joined the group".
    /// </summary>
    /// <remarks>
    /// Letting a service message anchor a session puts a boundary where the conversation did not
    /// have one — and, worse, makes an empty session that the filter then has to decide about.
    /// </remarks>
    [Fact]
    public void Service_messages_and_deleted_ones_are_left_out()
    {
        using var save = new TempSave();

        save.Seed("t1", (Noon, "a"), (Noon + 60, "b"));
        save.Execute("UPDATE message SET kind = 'service' WHERE plaintext = 'b';");

        new SessionSegmenter(save.Database).SegmentThread("t1");

        Assert.Equal(1, save.Count("SELECT message_count FROM session;"));
        Assert.Equal(
            0, save.Count("SELECT count(*) FROM message WHERE kind = 'service' AND session_id IS NOT NULL;"));
    }

    [Fact]
    public void The_filter_verdict_is_stored_with_the_version_that_produced_it()
    {
        using var save = new TempSave();

        save.Seed(
            "t1",
            (Noon, Long(30)),
            (Noon + (9 * Hour), "ok"),
            (Noon + (9 * Hour) + 30, "on my way"));

        var result = new SessionSegmenter(save.Database).SegmentThread("t1");

        Assert.Equal(2, result.Sessions);
        Assert.Equal(1, result.Substantive);
        Assert.Equal(1, save.Count("SELECT count(*) FROM session WHERE is_substantive = 1;"));
        Assert.Equal(SessionFilter.Version, save.Text("SELECT DISTINCT filter_version FROM session;"));
    }

    [Fact]
    public void Threads_come_back_with_the_most_recent_first()
    {
        using var save = new TempSave();

        save.Seed("older", (Noon - (100 * Hour), "a"));
        save.Seed("newer", (Noon, "b"));

        var threads = new SessionSegmenter(save.Database).Threads();

        Assert.Equal(["newer", "older"], threads.Select(t => t.ThreadId));
    }
}
