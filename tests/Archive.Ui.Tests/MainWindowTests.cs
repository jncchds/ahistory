using Archive.Ui.Services;
using Archive.Ui.ViewModels;
using Archive.Ui.Views;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Archive.Ui.Tests;

/// <summary>
/// The window actually opens and renders.
/// </summary>
/// <remarks>
/// Everything else here is view-model logic tested without a window. These few exist because a
/// binding typo, a missing data template or a style that fails to resolve compiles perfectly and
/// then produces an empty window at runtime — the one class of failure only a real Application
/// can catch.
/// </remarks>
public sealed class MainWindowTests
{
    private static MainWindowViewModel BuildViewModel(TempSave save) => new(
        save.Options,
        new OverviewViewModel(save.Queries),
        new ImportViewModel(save.Runner, new NullFolderPicker()),
        new PersonViewModel(save.Queries, save.Conversation),
        new PeopleViewModel(save.Queries, save.Merger),
        new ThreadsViewModel(save.Queries));

    [Fact]
    public Task The_window_opens_with_every_page_in_the_sidebar() => Headless.RunAsync(() =>
    {
        using var save = new TempSave();

        var window = new MainWindow { DataContext = BuildViewModel(save) };
        window.Show();

        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);

        Assert.Equal(
            ["Overview", "Import", "Conversations", "People", "Threads"],
            viewModel.Pages.Select(p => p.Title));

        Assert.Equal("Overview", viewModel.CurrentPage.Title);
    });

    /// <summary>
    /// Each page is chosen by its view-model type. A missing or mistyped data template shows up
    /// here as the page never becoming a view.
    /// </summary>
    [Fact]
    public Task Every_page_resolves_to_its_own_view() => Headless.RunAsync(() =>
    {
        using var save = new TempSave();

        var window = new MainWindow { DataContext = BuildViewModel(save) };
        window.Show();

        var viewModel = (MainWindowViewModel)window.DataContext!;

        var expected = new Dictionary<string, Type>
        {
            ["Overview"] = typeof(OverviewView),
            ["Import"] = typeof(ImportView),
            ["Conversations"] = typeof(PersonView),
            ["People"] = typeof(PeopleView),
            ["Threads"] = typeof(ThreadsView),
        };

        foreach (var page in viewModel.Pages)
        {
            viewModel.CurrentPage = page;
            window.UpdateLayout();

            var rendered = window.GetVisualDescendants().Any(v => v.GetType() == expected[page.Title]);

            Assert.True(rendered, $"The '{page.Title}' page did not render as {expected[page.Title].Name}.");
        }
    });

    /// <summary>An empty archive must look deliberate, not broken.</summary>
    [Fact]
    public Task An_empty_archive_says_so_rather_than_showing_nothing() => Headless.RunAsync(async () =>
    {
        using var save = new TempSave();

        var viewModel = BuildViewModel(save);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        await viewModel.CurrentPage.RefreshAsync();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .ToArray();

        Assert.Contains(texts, t => t.Contains("empty", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public Task The_window_shows_which_save_is_open() => Headless.RunAsync(() =>
    {
        using var save = new TempSave();

        var window = new MainWindow { DataContext = BuildViewModel(save) };
        window.Show();

        var viewModel = (MainWindowViewModel)window.DataContext!;

        Assert.Equal(save.Database.DatabasePath, viewModel.SavePath);
    });

    private sealed class NullFolderPicker : IFolderPicker
    {
        public Task<string?> PickAsync(string title) => Task.FromResult<string?>(null);
    }
}
