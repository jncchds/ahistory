using System.Collections.ObjectModel;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>
/// People, and the platform accounts attributed to them.
/// </summary>
/// <remarks>
/// <para>
/// One person at a time, with their accounts inside them. The page used to be two flat lists side
/// by side — people on the left, every account in the archive on the right — and you merged by
/// selecting a row in each and pressing a button at the bottom. The relationship that the whole
/// page is about, which accounts are one human, was the one thing it did not draw: you had to
/// match a name in one list against a line of small print in the other, and nothing on screen said
/// that the two selections were meant to combine.
/// </para>
/// <para>
/// So: a person is a card, their accounts are rows inside it, and an account moves by naming where
/// it should go. Nothing is a hidden pairing of two selections.
/// </para>
/// <para>
/// §1: merging is repointing identities, so everything here is reversible — except merging into the
/// owner, which needs an explicit confirmation because it makes everything that account ever said a
/// statement about you.
/// </para>
/// </remarks>
public sealed partial class PeopleViewModel(
    ArchiveQueries queries,
    IdentityMerger merger,
    MergeSuggestions suggestions,
    ILogger<PeopleViewModel>? logger = null)
    : ViewModelBase(logger)
{
    public override string Title => "People";

    public override string Glyph => "☺";

    public override int Position => 50;

    /// <summary>The people the list is showing, after the search box and the filter.</summary>
    public ObservableCollection<PersonRow> People { get; } = [];

    /// <summary>The selected person's accounts.</summary>
    public ObservableCollection<AccountItem> Accounts { get; } = [];

    /// <summary>Accounts elsewhere that might be the selected person too.</summary>
    public ObservableCollection<MatchItem> Matches { get; } = [];

    public IReadOnlyList<PeopleFilter> Filters { get; } =
    [
        new("Everyone", PeopleFilterKind.Everyone),
        new("With possible matches", PeopleFilterKind.WithPossibleMatches),
        new("With unconfirmed accounts", PeopleFilterKind.WithUnconfirmedAccounts),
    ];

    [ObservableProperty]
    private PersonRow? _selectedPerson;

    [ObservableProperty]
    private string? _search;

    [ObservableProperty]
    private PeopleFilter? _filter;

    /// <summary>What the selected person would be renamed to.</summary>
    [ObservableProperty]
    private string? _newName;

    /// <summary>Set when a merge needs confirming; the view shows a panel while it is non-null.</summary>
    [ObservableProperty]
    private string? _pendingOwnerMerge;

    /// <summary>What <see cref="ConfirmOwnerMerge"/> should do once the user says yes.</summary>
    private Func<Task>? _confirmedMerge;

    /// <summary>Everything loaded, before the list is narrowed. The detail pane reads from these.</summary>
    private IReadOnlyList<IdentityRow> _allIdentities = [];
    private IReadOnlyList<MergeSuggestion> _allSuggestions = [];
    private IReadOnlyList<PersonRow> _allPeople = [];

    /// <summary>How many pairs are waiting to be looked at, anywhere in the archive.</summary>
    public int MatchCount => _allSuggestions.Count;

    public bool HasMatches => MatchCount > 0;

    /// <summary>Nothing is selected, so the detail pane has nothing to draw.</summary>
    public bool HasSelection => SelectedPerson is not null;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var search = Search;

        var (people, identities, candidates) = await Task.Run(() =>
            (queries.People(search), queries.Identities(), suggestions.Candidates()))
            .ConfigureAwait(true);

        _allPeople = people;
        _allIdentities = identities;
        _allSuggestions = candidates;

        var previous = SelectedPerson?.Id;

        People.Clear();

        foreach (var person in Narrow(people))
        {
            People.Add(person);
        }

        // Stay with whoever was being looked at, so a merge does not move the reader somewhere
        // else mid-decision. Falling back to the first row keeps the detail pane populated.
        SelectedPerson = People.FirstOrDefault(p => p.Id == previous) ?? People.FirstOrDefault();

        // A selection that did not change still needs its accounts rebuilding: they are what the
        // merge just altered.
        LoadDetail();

        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(HasMatches));
    });

    partial void OnSearchChanged(string? value) => _ = RefreshAsync();

    partial void OnFilterChanged(PeopleFilter? value) => _ = RefreshAsync();

    partial void OnSelectedPersonChanged(PersonRow? value)
    {
        // The rename box follows the selection, so it can never rename the person before this one.
        NewName = value?.DisplayName;

        LoadDetail();

        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>
    /// Accepts a match: the two people become one.
    /// </summary>
    /// <remarks>
    /// A person merge rather than two identity merges, because either side may already carry
    /// several accounts — which is often why the pair turned up at all.
    /// </remarks>
    private Task AcceptAsync(MergeSuggestion suggestion)
    {
        // Whichever side is the owner is the one that survives: the archive's subject cannot be
        // dissolved into a contact (P5).
        var ownerIsLeft = _allPeople.Any(p => p.Id == suggestion.LeftPersonId && p.IsOwner);

        var (source, target) = suggestion.TouchesOwner && ownerIsLeft
            ? (suggestion.RightPersonId, suggestion.LeftPersonId)
            : (suggestion.LeftPersonId, suggestion.RightPersonId);

        if (suggestion.TouchesOwner)
        {
            Ask(
                $"Are {suggestion.LeftDisplayName} and {suggestion.RightDisplayName} both you? "
                + "Everything that account ever said becomes something you said, which the knowledge "
                + "base will treat as facts about your own life.",
                () => Task.Run(() => merger.MergePeople(source, target, confirmOwnerMerge: true)));

            return Task.CompletedTask;
        }

        return Apply(() => Task.Run(() => merger.MergePeople(source, target)));
    }

    /// <summary>
    /// Turns a match down for good.
    /// </summary>
    /// <remarks>
    /// Remembered, because a list that keeps re-offering what it has been told is wrong is one
    /// people stop reading — and the pairs it is most confident about are the ones it would keep
    /// putting back at the top.
    /// </remarks>
    private Task RejectAsync(MergeSuggestion suggestion) =>
        Apply(() => Task.Run(
            () => suggestions.Dismiss(suggestion.LeftIdentityId, suggestion.RightIdentityId)));

    /// <summary>Moves one account to another person.</summary>
    private Task MoveAsync(IdentityRow account, PersonRow target)
    {
        if (target.IsOwner)
        {
            Ask(
                $"Attribute {account.DisplayName} ({account.MessageCount:N0} messages) to you? "
                + "Everything this account ever said becomes something you said, which the knowledge "
                + "base will treat as facts about your own life.",
                () => Task.Run(() => merger.MergeInto(account.Id, target.Id, confirmOwnerMerge: true)));

            return Task.CompletedTask;
        }

        return Apply(() => Task.Run(() => merger.MergeInto(account.Id, target.Id)));
    }

    /// <summary>Takes an account off its person and gives it one of its own.</summary>
    private Task DetachAsync(IdentityRow account) =>
        Apply(() => Task.Run(() => merger.Unmerge(account.Id)));

    [RelayCommand]
    private async Task ConfirmOwnerMerge()
    {
        var confirmed = _confirmedMerge;

        PendingOwnerMerge = null;
        _confirmedMerge = null;

        if (confirmed is null)
        {
            return;
        }

        await Apply(confirmed).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelOwnerMerge()
    {
        PendingOwnerMerge = null;
        _confirmedMerge = null;

        // The dropdown that started it has already reset itself, but the row it belonged to may
        // now be showing a half-finished state.
        LoadDetail();
    }

    /// <summary>
    /// Renames a person.
    /// </summary>
    /// <remarks>
    /// Their accounts keep the names the platforms gave them. A person merged from three platforms
    /// is named after whichever import came first, which is rarely what you would call them.
    /// </remarks>
    [RelayCommand]
    private async Task Rename()
    {
        var person = SelectedPerson;
        var name = NewName?.Trim();

        if (person is null || string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, person.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        await Apply(() => Task.Run(() => merger.Rename(person.Id, name))).ConfigureAwait(true);
    }

    /// <summary>Rebuilds the detail pane for whoever is selected.</summary>
    private void LoadDetail()
    {
        Accounts.Clear();
        Matches.Clear();

        if (SelectedPerson is not { } person)
        {
            return;
        }

        var targets = _allPeople.Where(p => p.Id != person.Id).ToArray();

        foreach (var account in _allIdentities.Where(i => i.PersonId == person.Id))
        {
            Accounts.Add(new AccountItem(account, targets, MoveAsync, DetachAsync));
        }

        foreach (var suggestion in _allSuggestions.Where(s => Involves(s, person.Id)))
        {
            Matches.Add(new MatchItem(suggestion, person.Id, AcceptAsync, RejectAsync));
        }
    }

    private IEnumerable<PersonRow> Narrow(IReadOnlyList<PersonRow> people) =>
        (Filter?.Kind ?? PeopleFilterKind.Everyone) switch
        {
            PeopleFilterKind.WithPossibleMatches =>
                people.Where(p => _allSuggestions.Any(s => Involves(s, p.Id))),

            PeopleFilterKind.WithUnconfirmedAccounts =>
                people.Where(p => _allIdentities.Any(i => i.PersonId == p.Id && i.IsSynthetic)),

            _ => people,
        };

    private static bool Involves(MergeSuggestion suggestion, string personId) =>
        string.Equals(suggestion.LeftPersonId, personId, StringComparison.Ordinal)
        || string.Equals(suggestion.RightPersonId, personId, StringComparison.Ordinal);

    /// <summary>Holds a merge back behind a confirmation panel.</summary>
    private void Ask(string question, Func<Task> merge)
    {
        PendingOwnerMerge = question;
        _confirmedMerge = merge;
    }

    /// <summary>Runs a change and reloads, so the page never shows what is no longer true.</summary>
    private async Task Apply(Func<Task> change)
    {
        var ok = await RunAsync(change).ConfigureAwait(true);

        if (ok)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        else
        {
            // The change was refused — detaching the owner's own account, say. The lists are
            // still right; the detail pane may be showing a control mid-interaction.
            LoadDetail();
        }
    }
}
