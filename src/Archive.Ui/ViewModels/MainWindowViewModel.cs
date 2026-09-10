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
        PersonViewModel person,
        SearchViewModel searchPage,
        PeopleViewModel people,
        ThreadsViewModel threads)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        Pages = [overview, import, person, searchPage, people, threads];
        _currentPage = overview;

        // A search result is a place in the archive, not just a line of text. Opening one moves
        // to the conversation page and positions it on that message — which is navigation, so it
        // belongs to the window rather than to either page. Neither page learns the other exists.
        searchPage.OpenInConversationRequested += async (_, target) =>
        {
            CurrentPage = person;
            await person.RevealAsync(target.PersonId, target.MessageId).ConfigureAwait(true);
        };

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

    /// <summary>Just the file name: the rail is 78 pixels wide and a full path is unreadable there.</summary>
    public string SaveName => Path.GetFileNameWithoutExtension(_options.DatabasePath);

    [ObservableProperty]
    private ViewModelBase _currentPage;

    partial void OnCurrentPageChanged(ViewModelBase value) => _ = value.RefreshAsync();

    [RelayCommand]
    private async Task Loaded() => await CurrentPage.RefreshAsync().ConfigureAwait(true);
}
