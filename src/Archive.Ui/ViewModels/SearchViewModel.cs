using System.Collections.ObjectModel;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>A person the results can be narrowed to, plus "anyone".</summary>
public sealed record PersonFilterOption(string? Id, string Display);

public sealed partial class SearchViewModel(
    ArchiveQueries queries, ArchiveSearch search, ILogger<SearchViewModel>? logger = null)
    : ViewModelBase(logger)
{
    private const int ResultLimit = 200;

    public override string Title => "Search";

    public ObservableCollection<SearchHit> Results { get; } = [];

    public ObservableCollection<PersonFilterOption> People { get; } = [];

    [ObservableProperty]
    private string? _query;

    [ObservableProperty]
    private PersonFilterOption? _selectedPerson;

    [ObservableProperty]
    private long _totalMatches;

    [ObservableProperty]
    private bool _hasSearched;

    /// <summary>True when more matched than were shown.</summary>
    public bool IsTruncated => TotalMatches > Results.Count;

    public bool FoundNothing => HasSearched && Results.Count == 0 && !string.IsNullOrWhiteSpace(Query);

    /// <summary>
    /// The advice worth giving when a search finds nothing.
    /// </summary>
    /// <remarks>
    /// decisions.md D8: prefix expansion matches words starting with what was typed, so a stem
    /// finds every form built on it while one inflected form does not find another. That is a
    /// real limitation, and a user who does not know it concludes their archive is empty.
    /// </remarks>
    public string EmptyHint =>
        "Nothing matched. Search matches the start of words, so a shorter form finds more — "
        + "\"Праг\" finds \"Прага\" and \"Праге\", but \"Прага\" finds only itself.";

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var people = await Task.Run(() => queries.People()).ConfigureAwait(true);
        var previous = SelectedPerson?.Id;

        People.Clear();
        People.Add(new PersonFilterOption(null, "Anyone"));

        foreach (var person in people)
        {
            People.Add(new PersonFilterOption(person.Id, person.DisplayName));
        }

        SelectedPerson = People.FirstOrDefault(p => p.Id == previous) ?? People[0];

        // A refresh after an import must not silently show results from before it.
        if (HasSearched)
        {
            await RunSearchAsync().ConfigureAwait(true);
        }
    });

    [RelayCommand]
    private Task Run() => RunSearchAsync();

    private Task RunSearchAsync() => RunAsync(async () =>
    {
        var query = Query;
        var filter = new SearchFilter(PersonId: SelectedPerson?.Id);

        Results.Clear();
        HasSearched = true;

        if (string.IsNullOrWhiteSpace(query))
        {
            TotalMatches = 0;
            Notify();
            return;
        }

        // Off the UI thread like every other query: search runs over the whole archive, and a
        // frozen window is indistinguishable from a crashed one.
        var (hits, total) = await Task.Run(() =>
            (search.Search(query, filter, ResultLimit), search.Count(query, filter))).ConfigureAwait(true);

        foreach (var hit in hits)
        {
            Results.Add(hit);
        }

        TotalMatches = total;
        Notify();
    });

    private void Notify()
    {
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(FoundNothing));
    }
}
