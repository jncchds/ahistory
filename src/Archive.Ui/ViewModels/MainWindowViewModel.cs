using System.Collections.ObjectModel;
using Archive.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archive.Ui.ViewModels;

/// <summary>The window: a sidebar of pages, and whichever one is showing.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ArchiveOptions _options;

    public MainWindowViewModel(
        ArchiveOptions options,
        OverviewViewModel overview,
        ImportViewModel import,
        PeopleViewModel people,
        ThreadsViewModel threads)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        Pages = [overview, import, people, threads];
        _currentPage = overview;

        // An import changes what every other page shows, so they are told rather than left to
        // notice. Without this the overview keeps reporting the counts from before the import.
        import.Imported += async () =>
        {
            foreach (var page in Pages)
            {
                await page.RefreshAsync().ConfigureAwait(true);
            }
        };
    }

    public ObservableCollection<ViewModelBase> Pages { get; }

    public string SavePath => _options.DatabasePath;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    partial void OnCurrentPageChanged(ViewModelBase value) => _ = value.RefreshAsync();

    [RelayCommand]
    private async Task Loaded() => await CurrentPage.RefreshAsync().ConfigureAwait(true);
}
