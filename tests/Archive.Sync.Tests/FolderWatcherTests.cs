namespace Archive.Sync.Tests;

/// <summary>
/// Watched folders: what a scheduled export lands in, re-read when it changes and left alone when
/// it has not.
/// </summary>
public sealed class FolderWatcherTests
{
    [Fact]
    public async Task A_watched_folder_is_read_the_first_time_it_is_checked()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher();

        watcher.Add(new WatchedFolder(save.Export("telegram", (1, "morning"))));

        var check = Assert.Single(await watcher.CheckAllAsync());

        Assert.Equal(FolderCheckOutcome.Imported, check.Outcome);
        Assert.Equal(1, check.Stats!.MessagesInserted);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    /// <summary>
    /// The point of the fingerprint: a folder nobody has touched costs one directory listing, not
    /// a read of every file in it.
    /// </summary>
    [Fact]
    public async Task An_unchanged_folder_is_not_read_again()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher();

        watcher.Add(new WatchedFolder(save.Export("telegram", (1, "morning"))));

        await watcher.CheckAllAsync();
        var second = Assert.Single(await watcher.CheckAllAsync());

        Assert.Equal(FolderCheckOutcome.Unchanged, second.Outcome);
        Assert.Null(second.Stats);

        // One completed run, not two.
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM import WHERE status = 'completed';"));
    }

    [Fact]
    public async Task A_folder_that_has_grown_is_read_again_and_adds_only_what_is_new()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher();

        var folder = save.Export("telegram", (1, "morning"));
        watcher.Add(new WatchedFolder(folder));
        await watcher.CheckAllAsync();

        // What a scheduled export does: the same chat again, with tonight's messages on the end.
        save.Export("telegram", (1, "morning"), (2, "evening"));

        var second = Assert.Single(await watcher.CheckAllAsync());

        Assert.Equal(FolderCheckOutcome.Imported, second.Outcome);
        Assert.Equal(1, second.Stats!.MessagesInserted);
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    /// <summary>An unplugged drive is a normal state, not a failure to report as an error.</summary>
    [Fact]
    public async Task A_folder_that_is_not_there_is_reported_rather_than_thrown()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher();

        watcher.Add(new WatchedFolder(Path.Combine(save.EmptyFolder("gone"), "missing")));

        var check = Assert.Single(await watcher.CheckAllAsync());

        Assert.Equal(FolderCheckOutcome.Missing, check.Outcome);
    }

    /// <summary>
    /// One backup that will not read must not stop the others being read.
    /// </summary>
    /// <remarks>
    /// A reader refuses what it does not understand (D20), and a folder is often looked at while a
    /// sync client is halfway through writing it. So a failure is recorded against that folder and
    /// the run carries on — and it is tried again at the next change, by which time the file is
    /// usually complete.
    /// </remarks>
    [Fact]
    public async Task A_folder_that_fails_to_read_is_reported_and_the_others_are_still_read()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher();

        watcher.Add(new WatchedFolder(save.BrokenExport("half-written")));
        watcher.Add(new WatchedFolder(save.Export("telegram", (1, "morning"))));

        var checks = await watcher.CheckAllAsync();

        Assert.Equal(FolderCheckOutcome.Failed, checks[0].Outcome);
        Assert.NotNull(checks[0].Error);
        Assert.Equal(FolderCheckOutcome.Imported, checks[1].Outcome);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    [Fact]
    public async Task A_folder_that_is_no_longer_watched_is_not_checked()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher();

        var folder = save.Export("telegram", (1, "morning"));

        watcher.Add(new WatchedFolder(folder));
        watcher.Remove(folder);

        Assert.Empty(watcher.Folders);
        Assert.Empty(await watcher.CheckAllAsync());
    }

    /// <summary>Watching the same folder twice is one entry, not two imports of everything.</summary>
    [Fact]
    public void Watching_a_folder_already_watched_replaces_its_entry()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher();

        var folder = save.Export("telegram", (1, "morning"));

        watcher.Add(new WatchedFolder(folder));
        watcher.Add(new WatchedFolder(folder, Platform: "telegram", SourceId: "telegram:account:5001"));

        var only = Assert.Single(watcher.Folders);

        Assert.Equal("telegram", only.Platform);
        Assert.Equal("telegram:account:5001", only.SourceId);
    }

    /// <summary>
    /// A file appearing in a watched folder starts an import, once the folder stops changing.
    /// </summary>
    /// <remarks>
    /// The quiet period is what keeps a burst of writes from being read halfway through, so the
    /// test uses a short one rather than none: with no wait at all this would pass while the real
    /// behaviour — read after things settle — went untested.
    /// </remarks>
    [Fact]
    public async Task A_change_in_a_watched_folder_is_read_once_the_folder_goes_quiet()
    {
        using var save = new TempSave();
        using var watcher = save.Watcher(quiet: TimeSpan.FromMilliseconds(150), poll: TimeSpan.FromMinutes(30));

        var folder = save.Export("telegram", (1, "morning"));
        watcher.Add(new WatchedFolder(folder));

        var imported = new TaskCompletionSource<FolderCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;

        watcher.Checked += check =>
        {
            // The first pass is the one Start schedules, which imports what is already there.
            if (Interlocked.Increment(ref seen) > 1 && check.Outcome == FolderCheckOutcome.Imported)
            {
                imported.TrySetResult(check);
            }
        };

        watcher.Start();

        // Let the first pass finish before changing anything, so the change is what causes the
        // second one rather than racing it.
        while (Volatile.Read(ref seen) == 0)
        {
            await Task.Delay(20);
        }

        save.Export("telegram", (1, "morning"), (2, "evening"));

        var completed = await Task.WhenAny(imported.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(completed == imported.Task, "The watcher did not import after the folder changed.");
        Assert.Equal(1, (await imported.Task).Stats!.MessagesInserted);
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM message;"));
    }
}
