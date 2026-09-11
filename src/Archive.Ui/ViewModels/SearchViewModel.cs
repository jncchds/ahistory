using System.Collections.ObjectModel;
using Archive.Ai.Search;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>A person the results can be narrowed to, plus "anyone".</summary>
public sealed record PersonFilterOption(string? Id, string Display);

/// <summary>A place in the archive to go to: one message, in one person's conversation.</summary>
public sealed record ConversationTarget(string PersonId, long MessageId);

/// <summary>
/// One line of the conversation a result was said in.
/// </summary>
/// <remarks>
/// §4: a line out of context is often nonsense — "yeah exactly" is the standing example — and a
/// list of matches with nothing around them makes the reader open each one to find out which is
/// the one they meant. A few lines either side is usually enough to tell.
/// </remarks>
public sealed record SearchPreviewRow(PersonMessageRow Row, bool IsHit)
{
    public string SenderLabel => Row.SenderLabel;

    public string Text => Row.Plaintext;

    public string Timestamp =>
        DateTimeOffset.FromUnixTimeSeconds(Row.SentAtUnix).LocalDateTime.ToString("d MMM yyyy, HH:mm");
}

public sealed partial class SearchViewModel(
    ArchiveQueries queries,
    ArchiveSearch search,
    PersonConversation conversation,
    ILogger<SearchViewModel>? logger = null,
    HybridSearch? hybrid = null)
    : ViewModelBase(logger)
{
    /// <summary>
    /// Whether the user has chosen, this session, to search by meaning or not.
    /// </summary>
    /// <remarks>
    /// Until they do, the toggle follows coverage — on once enough of the archive is embedded to
    /// help (ai-plan.md §10). After they do, their choice stands.
    /// </remarks>
    private bool _meaningChosen;

    private bool _settingMeaning;

    /// <summary>
    /// True when search by meaning can run: AI on, the endpoint agreed to, and vectors to search.
    /// </summary>
    /// <remarks>
    /// False means the toggle is not drawn at all. Zero coverage is silent — a search bar that
    /// explains what the user is missing is exactly what P1 refuses to have.
    /// </remarks>
    [ObservableProperty]
    private bool _canSearchByMeaning;

    [ObservableProperty]
    private bool _searchByMeaning;

    partial void OnSearchByMeaningChanged(bool value)
    {
        if (!_settingMeaning)
        {
            _meaningChosen = true;
        }
    }
    private const int ResultLimit = 200;

    /// <summary>How many lines either side of a hit the preview shows.</summary>
    /// <remarks>
    /// Small on purpose. This is here to identify a result, not to be read in — reading it is
    /// what opening the conversation is for.
    /// </remarks>
    private const int PreviewRadius = 3;

    /// <summary>Raised when a result should be opened where it was said.</summary>
    public event EventHandler<ConversationTarget>? OpenInConversationRequested;

    public override string Title => "Search";

    public override string Glyph => "⌕";

    public override int Position => 40;

    public ObservableCollection<SearchHit> Results { get; } = [];

    public ObservableCollection<PersonFilterOption> People { get; } = [];

    /// <summary>What was said around the selected result, the result itself included.</summary>
    public ObservableCollection<SearchPreviewRow> Preview { get; } = [];

    /// <summary>
    /// The preview fetch the current selection started, if it is still running.
    /// </summary>
    /// <remarks>
    /// Selecting a result is a property change — that is what a list binding does — so the fetch
    /// it starts has nowhere to be awaited. Anything that needs the preview to have arrived waits
    /// on this rather than on a delay.
    /// </remarks>
    public Task PendingPreview { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private string? _query;

    [ObservableProperty]
    private PersonFilterOption? _selectedPerson;

    [ObservableProperty]
    private SearchHit? _selectedHit;

    [ObservableProperty]
    private long _totalMatches;

    /// <summary>False when the total was cut off at the cap and is really "that many or more".</summary>
    [ObservableProperty]
    private bool _totalIsExact = true;

    [ObservableProperty]
    private bool _hasSearched;

    /// <summary>True when more matched than were shown.</summary>
    public bool IsTruncated => TotalMatches > Results.Count;

    /// <summary>What the result header says, exact or capped.</summary>
    public string ResultSummary =>
        TotalIsExact
            ? $"{TotalMatches:N0} matches — showing the best {Results.Count:N0}"
            : $"{TotalMatches:N0}+ matches — showing the best {Results.Count:N0}";

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

        if (hybrid is not null)
        {
            var availability = await Task.Run(hybrid.Availability).ConfigureAwait(true);

            CanSearchByMeaning = availability.IsAvailable;

            if (!_meaningChosen)
            {
                _settingMeaning = true;
                SearchByMeaning = availability.Recommended;
                _settingMeaning = false;
            }
        }

        // A refresh after an import must not silently show results from before it.
        if (HasSearched)
        {
            await RunSearchAsync().ConfigureAwait(true);
        }
    });

    partial void OnSelectedHitChanged(SearchHit? value)
    {
        if (value is null)
        {
            Preview.Clear();
            return;
        }

        PendingPreview = LoadPreviewAsync(value);
    }

    [RelayCommand]
    private Task Run() => RunSearchAsync();

    /// <summary>
    /// Opens the selected result in the conversation it belongs to.
    /// </summary>
    /// <remarks>
    /// Which conversation that is takes a lookup: a direct message is read in the conversation of
    /// whoever is at the other end of the thread, not of whoever sent it, so your own line comes
    /// back attributed to them.
    /// </remarks>
    [RelayCommand]
    private Task OpenInConversation() => RunAsync(async () =>
    {
        if (SelectedHit is not { } hit)
        {
            return;
        }

        var personId = await Task.Run(() => conversation.PersonOf(hit.MessageId)).ConfigureAwait(true);

        if (personId is null)
        {
            // A message whose sender has not been attributed to anyone has no conversation to be
            // read in yet. Saying so beats a button that silently does nothing.
            Error = "This message is not attributed to anyone yet, so there is no conversation to open.";
            return;
        }

        OpenInConversationRequested?.Invoke(this, new ConversationTarget(personId, hit.MessageId));
    });

    private Task LoadPreviewAsync(SearchHit hit) => RunAsync(async () =>
    {
        Preview.Clear();

        var around = await Task.Run(() => conversation.Context(hit.MessageId, PreviewRadius))
            .ConfigureAwait(true);

        foreach (var row in around)
        {
            Preview.Add(new SearchPreviewRow(row, row.Id == hit.MessageId));
        }
    });

    private Task RunSearchAsync() => RunAsync(async () =>
    {
        var query = Query;
        var filter = new SearchFilter(PersonId: SelectedPerson?.Id);

        Results.Clear();
        Preview.Clear();
        SelectedHit = null;
        HasSearched = true;

        if (string.IsNullOrWhiteSpace(query))
        {
            TotalMatches = 0;
            Notify();
            return;
        }

        // By meaning as well, when that can run and is wanted. The keyword half inside it is the
        // same search as below; this only adds a second list and merges the two.
        if (hybrid is not null && CanSearchByMeaning && SearchByMeaning)
        {
            var merged = await Task.Run(() => hybrid.SearchAsync(query, filter, ResultLimit)).ConfigureAwait(true);

            foreach (var hit in merged)
            {
                Results.Add(hit);
            }

            TotalMatches = merged.Count;
            TotalIsExact = true;
            Notify();
            return;
        }

        // Off the UI thread like every other query: search runs over the whole archive, and a
        // frozen window is indistinguishable from a crashed one.
        //
        // The count is a second pass over the same matches, and it is only needed when the
        // results were truncated — a search returning fewer than the limit has already counted
        // itself. On a very common word that halves the work; on everything else it removes it.
        var (hits, total, exact) = await Task.Run(() =>
        {
            var results = search.Search(query, filter, ResultLimit);

            // A result set shorter than the limit has already counted itself.
            if (results.Count < ResultLimit)
            {
                return (results, (long)results.Count, true);
            }

            var (count, isExact) = search.Count(query, filter);

            return (results, count, isExact);
        }).ConfigureAwait(true);

        foreach (var hit in hits)
        {
            Results.Add(hit);
        }

        TotalMatches = total;
        TotalIsExact = exact;
        Notify();
    });

    private void Notify()
    {
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(FoundNothing));
    }
}
