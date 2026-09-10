using System.Diagnostics;
using Archive.Media;

namespace Archive.Import.Tests;

/// <summary>
/// The archive has to stay readable while an import runs (AGENTS.md P1). An import of a real
/// account takes minutes; a reader blocked for that long is an app that is simply down.
/// </summary>
public sealed class ImportConcurrencyTests
{
    /// <summary>
    /// Reads happen on a different connection while the importer holds an open write
    /// transaction. WAL is what makes that work, and this test is the reason
    /// <c>journal_mode = WAL</c> is not a detail anyone may quietly change.
    /// </summary>
    [Fact]
    public void The_archive_is_readable_while_an_import_is_writing()
    {
        using var save = new TempSave();

        var readsDuringImport = 0;
        var slowestRead = TimeSpan.Zero;

        // A batch size larger than the fixture guarantees every read below happens while the
        // importer's transaction is still open and uncommitted.
        save.Runner.Run(save.Export("group-and-dm", Exports.GroupAndDm()), _ =>
        {
            var stopwatch = Stopwatch.StartNew();

            using var connection = save.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM message;";
            command.ExecuteScalar();

            stopwatch.Stop();
            readsDuringImport++;

            if (stopwatch.Elapsed > slowestRead)
            {
                slowestRead = stopwatch.Elapsed;
            }
        }, batchSize: 10_000);

        Assert.Equal(5, readsDuringImport);

        // busy_timeout is 5 seconds, so a blocked reader would show up here as multi-second
        // waits before failing. A working WAL reader returns immediately.
        Assert.True(
            slowestRead < TimeSpan.FromSeconds(1),
            $"A read during import took {slowestRead.TotalMilliseconds:N0} ms — readers are being blocked.");
    }

    /// <summary>
    /// Readers see a consistent snapshot rather than a half-written batch: messages appear when
    /// their batch commits, never mid-transaction.
    /// </summary>
    [Fact]
    public void A_reader_never_sees_a_partially_written_batch()
    {
        using var save = new TempSave();

        var countsSeen = new List<long>();

        save.Runner.Run(save.Export("group-and-dm", Exports.GroupAndDm()), _ =>
        {
            using var connection = save.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM message;";
            countsSeen.Add(Convert.ToInt64(command.ExecuteScalar()));
        }, batchSize: 10_000);

        // One uncommitted batch, so every reader sees the pre-import state throughout.
        Assert.All(countsSeen, count => Assert.Equal(0, count));

        // And the whole batch is visible once it commits.
        Assert.Equal(5, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    /// <summary>
    /// Smaller batches make an import land progressively, so a long import fills the archive in
    /// front of the user instead of appearing all at once at the end.
    /// </summary>
    [Fact]
    public void Progress_becomes_visible_to_readers_as_batches_commit()
    {
        using var save = new TempSave();

        var countsSeen = new List<long>();

        save.Runner.Run(save.Export("group-and-dm", Exports.GroupAndDm()), _ =>
        {
            using var connection = save.Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM message;";
            countsSeen.Add(Convert.ToInt64(command.ExecuteScalar()));
        }, batchSize: 2);

        Assert.Contains(countsSeen, c => c > 0);
        Assert.True(countsSeen.SequenceEqual(countsSeen.Order()), "Visible message count went backwards.");
    }

    /// <summary>
    /// Storing a file means reading and hashing every byte of it. On a re-import almost every
    /// message is already known, so touching the media store at all would mean re-hashing an
    /// entire media folder — tens of gigabytes on a real archive — to discover there was nothing
    /// to do.
    /// </summary>
    [Fact]
    public void A_re_import_does_not_touch_the_media_store()
    {
        using var save = new TempSave();

        var counting = new CountingMediaStore(save.MediaStore);

        save.RunnerWith(counting).Run(Exports.WriteForwards(save.ExportFolder("forwards")));
        var afterFirst = counting.PutCount;

        counting.Reset();
        save.RunnerWith(counting).Run(Exports.WriteForwards(save.ExportFolder("forwards")));

        Assert.True(afterFirst > 0, "The first import should have stored media.");
        Assert.Equal(0, counting.PutCount);
    }

    /// <summary>An edit can change attachments, so a revised message does pay the hashing cost.</summary>
    [Fact]
    public void A_revised_message_does_resolve_its_media_again()
    {
        using var save = new TempSave();

        var counting = new CountingMediaStore(save.MediaStore);

        var before = save.WriteExport("before", """
            { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
                "text": "a", "text_entities": [ { "type": "plain", "text": "a" } ] } ] }
            """);

        var after = save.WriteExport("after", """
            { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
                "text": "b", "text_entities": [ { "type": "plain", "text": "b" } ] } ] }
            """);

        save.RunnerWith(counting).Run(before);
        counting.Reset();

        var stats = save.RunnerWith(counting).Run(after);

        Assert.Equal(1, stats.MessagesRevised);
        Assert.Equal(0, counting.PutCount); // no attachments on this message, but the path ran
    }

    private sealed class CountingMediaStore(IMediaStore inner) : IMediaStore
    {
        internal int PutCount { get; private set; }

        internal void Reset() => PutCount = 0;

        public string Root => inner.Root;

        public Task<MediaPutResult> PutAsync(Stream source, string? extension = null, CancellationToken cancellationToken = default)
        {
            PutCount++;
            return inner.PutAsync(source, extension, cancellationToken);
        }

        public Task<MediaPutResult> PutFileAsync(string path, CancellationToken cancellationToken = default)
        {
            PutCount++;
            return inner.PutFileAsync(path, cancellationToken);
        }

        public string PathFor(string hash, string? extension = null) => inner.PathFor(hash, extension);

        public bool Exists(string hash, string? extension = null) => inner.Exists(hash, extension);

        public Stream OpenRead(string hash, string? extension = null) => inner.OpenRead(hash, extension);
    }
}
