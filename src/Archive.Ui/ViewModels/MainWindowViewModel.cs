using System.Collections.ObjectModel;
using System.ComponentModel;
using Archive.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archive.Ui.ViewModels;

/// <summary>The window: a sidebar of pages, and whichever one is showing.</summary>
/// <remarks>
/// The pages arrive as a collection rather than as named constructor parameters, so that an
/// optional feature can add one without this class being edited — and, more to the point, without
/// it being able to depend on that feature existing. What it still does is wire the two places
/// where one page has to reach another, which is navigation and therefore belongs to the window.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ArchiveOptions _options;
    private readonly IReadOnlyList<ViewModelBase> _all;

    public MainWindowViewModel(ArchiveOptions options, IEnumerable<ViewModelBase> pages)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pages);

        _options = options;
        _all = [.. pages.OrderBy(page => page.Position)];

        if (_all.Count == 0)
        {
            throw new ArgumentException("The window needs at least one page.", nameof(pages));
        }

        Pages = [.. _all.Where(page => page.IsAvailable)];
        _currentPage = Pages.Count > 0 ? Pages[0] : _all[0];

        foreach (var page in _all)
        {
            // A page can become available or stop being so while the app runs — switching AI off
            // takes its pages out of the rail there and then rather than at the next restart.
            page.PropertyChanged += OnPageChanged;
        }

        // A search result is a place in the archive, not just a line of text. Opening one moves
        // to the conversation page and positions it on that message — which is navigation, so it
        // belongs to the window rather than to either page. Neither page learns the other exists.
        var search = _all.OfType<SearchViewModel>().FirstOrDefault();
        var person = _all.OfType<PersonViewModel>().FirstOrDefault();

        if (search is not null && person is not null)
        {
            search.OpenInConversationRequested += async (_, target) =>
            {
                CurrentPage = person;
                await person.RevealAsync(target.PersonId, target.MessageId).ConfigureAwait(true);
            };
        }

        // A diary sentence is a place in the archive in the same way: its source opens where it
        // was said.
        if (_all.OfType<DiaryViewModel>().FirstOrDefault() is { } diary && person is not null)
        {
            diary.OpenInConversationRequested += async (_, target) =>
            {
                CurrentPage = person;
                await person.RevealAsync(target.PersonId, target.MessageId).ConfigureAwait(true);
            };
        }

        // An import changes what every other page shows, so they are told rather than left to
        // notice. Without this the overview keeps reporting the counts from before the import.
        if (_all.OfType<ImportViewModel>().FirstOrDefault() is { } import)
        {
            import.Imported += async () =>
            {
                foreach (var page in _all)
                {
                    await page.RefreshAsync().ConfigureAwait(true);
                }
            };
        }
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

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModelBase.IsAvailable))
        {
            SyncPages();
        }
    }

    /// <summary>
    /// Brings the rail in line with which pages are currently available.
    /// </summary>
    /// <remarks>
    /// Edited in place rather than rebuilt. Clearing an ObservableCollection bound to a ListBox
    /// drops its selection, so a page appearing at the bottom of the rail would send the reader
    /// back to the overview — from a conversation they were in the middle of.
    /// </remarks>
    private void SyncPages()
    {
        var wanted = _all.Where(page => page.IsAvailable).ToList();

        for (var i = Pages.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Pages[i]))
            {
                Pages.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= Pages.Count || !ReferenceEquals(Pages[i], wanted[i]))
            {
                Pages.Insert(i, wanted[i]);
            }
        }

        // The page being read has just been taken away. Somewhere is better than nowhere.
        if (!Pages.Contains(CurrentPage) && Pages.Count > 0)
        {
            CurrentPage = Pages[0];
        }
    }
}
