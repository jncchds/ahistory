namespace Archive.Sync.Tests;

/// <summary>
/// The per-machine settings file: what it keeps, and what it refuses to keep.
/// </summary>
public sealed class SyncSettingsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private SyncSettingsStore Store() => new(_directory);

    [Fact]
    public void Nothing_is_watched_and_nothing_is_connected_until_it_is_said_so()
    {
        var settings = Store().Load();

        Assert.Empty(settings.WatchedFolders);
        Assert.False(settings.Telegram.Enabled);
        Assert.False(settings.Telegram.Live);
        Assert.False(settings.Telegram.HasApplication);
    }

    [Fact]
    public void Watched_folders_belong_to_the_save_they_import_into()
    {
        var store = Store();

        store.Update(s => s.Watch(Path.Combine(_directory, "mine.db"), new WatchedFolder(_directory)));

        Assert.Single(store.Load().FoldersFor(Path.Combine(_directory, "mine.db")));
        Assert.Empty(store.Load().FoldersFor(Path.Combine(_directory, "someone-elses.db")));
    }

    [Fact]
    public void What_was_saved_is_what_is_read_back()
    {
        var store = Store();
        var save = Path.Combine(_directory, "mine.db");

        store.Update(s =>
        {
            s.Watch(save, new WatchedFolder(_directory, "telegram", "telegram:account:5001", "5001"));
            s.Telegram.Enabled = true;
            s.Telegram.ApiId = 1234;
            s.Telegram.ApiHash = "hash";
        });

        var reloaded = Store().Load();
        var folder = Assert.Single(reloaded.FoldersFor(save));

        Assert.Equal("telegram", folder.Platform);
        Assert.Equal("telegram:account:5001", folder.SourceId);
        Assert.Equal("5001", folder.OwnerAccountId);
        Assert.True(reloaded.Telegram.HasApplication);
    }

    /// <summary>
    /// A corrupt file must not take the app with it, and must not leave anything switched on.
    /// </summary>
    /// <remarks>
    /// The same reasoning as <c>ai.json</c>, with one addition: failing open here would mean
    /// contacting a platform because a file had a stray comma in it.
    /// </remarks>
    [Fact]
    public void A_corrupt_settings_file_reads_as_everything_off()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "sync.json"), "{ not json");

        var settings = Store().Load();

        Assert.False(settings.Telegram.Enabled);
        Assert.Empty(settings.WatchedFolders);
    }

    [Fact]
    public void Removing_a_folder_leaves_the_others()
    {
        var store = Store();
        var save = Path.Combine(_directory, "mine.db");
        var kept = Directory.CreateDirectory(Path.Combine(_directory, "kept")).FullName;
        var dropped = Directory.CreateDirectory(Path.Combine(_directory, "dropped")).FullName;

        store.Update(s =>
        {
            s.Watch(save, new WatchedFolder(kept));
            s.Watch(save, new WatchedFolder(dropped));
            s.Unwatch(save, dropped);
        });

        var only = Assert.Single(store.Load().FoldersFor(save));

        Assert.Equal(kept, only.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
