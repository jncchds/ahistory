using Archive.Data;
using Archive.Ui.ViewModels;
using Archive.Ui.Views;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Archive.Ui.Tests;

/// <summary>
/// The prompt shown when a save was made by an older version of ahistory.
/// </summary>
/// <remarks>
/// The desktop head treats a failure to open a save as fatal, so without this the answer to "your
/// save is behind" would be a window that never appears and a line in a log file. What is checked
/// here is that the question is asked, that asking it changes nothing on its own, and that saying
/// yes actually leads to the archive.
/// </remarks>
public sealed class UpgradeWindowTests
{
    /// <summary>
    /// Puts a save back to where it stood before <c>003_search.sql</c> ran, objects and all.
    /// </summary>
    private static void Rewind(TempSave save)
    {
        using var connection = save.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER trg_message_ai;
            DROP TRIGGER trg_message_au;
            DROP TRIGGER trg_search_document_ai;
            DROP TRIGGER trg_search_document_ad;
            DROP TRIGGER trg_search_document_au;
            DROP TABLE search_fts;
            DROP TABLE search_document;
            DROP TABLE save_provenance;
            DROP TABLE merge_dismissal;
            DELETE FROM schema_migration
            WHERE name IN ('003_search.sql', '004_provenance.sql', '005_merge_suggestions.sql');
            """;
        command.ExecuteNonQuery();
    }

    private static UpgradeViewModel ViewModelFor(TempSave save)
    {
        var status = save.Database.Inspect();

        Assert.Equal(SchemaState.Behind, status.State);

        return new UpgradeViewModel(
            save.Database, status.Pending, save.Database.DatabasePath + ".pre-003");
    }

    [Fact]
    public Task The_prompt_names_the_save_and_what_would_run() => Headless.RunAsync(() =>
    {
        using var save = new TempSave();
        Rewind(save);

        var window = new UpgradeWindow { DataContext = ViewModelFor(save) };
        window.Show();
        window.UpdateLayout();

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty).ToArray();

        Assert.Contains(text, t => t.Contains("003_search.sql", StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains(save.Database.DatabasePath, StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains("cannot be undone", StringComparison.Ordinal));
    });

    /// <summary>
    /// Showing the prompt does not migrate anything. The save is only touched when asked.
    /// </summary>
    [Fact]
    public Task Showing_the_prompt_changes_nothing() => Headless.RunAsync(() =>
    {
        using var save = new TempSave();
        Rewind(save);

        var window = new UpgradeWindow { DataContext = ViewModelFor(save) };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(SchemaState.Behind, save.Database.Inspect().State);
    });

    [Fact]
    public Task Accepting_upgrades_the_save_and_copies_it_first() => Headless.RunAsync(async () =>
    {
        using var save = new TempSave();
        Rewind(save);

        var viewModel = ViewModelFor(save);
        var window = new UpgradeWindow { DataContext = viewModel };
        window.Show();

        await viewModel.UpgradeCommand.ExecuteAsync(null);

        Assert.Null(viewModel.Error);
        Assert.Equal(SchemaState.UpToDate, save.Database.Inspect().State);
        Assert.True(File.Exists(viewModel.BackupPath));
    });

    /// <summary>
    /// The copy is skippable, because a large archive is a large copy and it is the user's disk.
    /// </summary>
    [Fact]
    public Task Declining_the_copy_still_upgrades() => Headless.RunAsync(async () =>
    {
        using var save = new TempSave();
        Rewind(save);

        var viewModel = ViewModelFor(save);
        viewModel.BackUpFirst = false;

        await viewModel.UpgradeCommand.ExecuteAsync(null);

        Assert.Equal(SchemaState.UpToDate, save.Database.Inspect().State);
        Assert.False(File.Exists(viewModel.BackupPath));
    });

    /// <summary>
    /// Saying yes leads to the archive.
    /// </summary>
    /// <remarks>
    /// The transition is the part that fails silently: the prompt is the application's main
    /// window, so closing it before another is open ends the process, and a user who agreed to
    /// upgrade would watch the app vanish having done exactly what was asked.
    /// </remarks>
    [Fact]
    public Task Upgrading_opens_the_archive_and_closes_the_prompt() => Headless.RunAsync(async () =>
    {
        using var save = new TempSave();
        Rewind(save);

        var viewModel = ViewModelFor(save);
        var window = new UpgradeWindow { DataContext = viewModel };

        Window? opened = null;

        window.ContinueWith(() =>
        {
            opened = new Window();
            return opened;
        });

        window.Show();

        await viewModel.UpgradeCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.True(opened!.IsVisible);
        Assert.False(window.IsVisible);
    });
}
