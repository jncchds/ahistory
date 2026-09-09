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
/// §1: merging is repointing identities, so everything here is reversible. The one exception is
/// merging into the owner, which needs an explicit confirmation because it makes everything that
/// contact ever said a statement about you.
/// </remarks>
public sealed partial class PeopleViewModel(
    ArchiveQueries queries, IdentityMerger merger, ILogger<PeopleViewModel>? logger = null)
    : ViewModelBase(logger)
{
    public override string Title => "People";

    public override string Glyph => "☺";

    public ObservableCollection<PersonRow> People { get; } = [];

    public ObservableCollection<IdentityRow> Identities { get; } = [];

    [ObservableProperty]
    private PersonRow? _selectedPerson;

    [ObservableProperty]
    private IdentityRow? _selectedIdentity;

    [ObservableProperty]
    private bool _showOnlyUncertain;

    /// <summary>Set when a merge needs confirming; the view shows a panel while it is non-null.</summary>
    [ObservableProperty]
    private string? _pendingOwnerMerge;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var onlyUncertain = ShowOnlyUncertain;

        var (people, identities) = await Task.Run(() =>
            (queries.People(), queries.Identities(onlyUncertain))).ConfigureAwait(true);

        Replace(People, people);
        Replace(Identities, identities);
    });

    partial void OnShowOnlyUncertainChanged(bool value) => _ = RefreshAsync();

    /// <summary>
    /// Attributes the selected account to the selected person.
    /// </summary>
    /// <remarks>
    /// A merge into the owner is not performed here — it sets <see cref="PendingOwnerMerge"/> and
    /// waits. §1: getting this wrong poisons the knowledge base, and a list is easy to mis-click.
    /// </remarks>
    [RelayCommand]
    private async Task Merge()
    {
        var identity = SelectedIdentity;
        var person = SelectedPerson;

        if (identity is null || person is null)
        {
            return;
        }

        if (person.IsOwner)
        {
            PendingOwnerMerge =
                $"Attribute {identity.DisplayName} ({identity.MessageCount:N0} messages) to you? "
                + "Everything this account ever said becomes something you said, which the knowledge "
                + "base will treat as facts about your own life.";

            return;
        }

        var ok = await RunAsync(async () =>
        {
            await Task.Run(() => merger.MergeInto(identity.Id, person.Id)).ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (ok)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ConfirmOwnerMerge()
    {
        var identity = SelectedIdentity;
        var person = SelectedPerson;
        PendingOwnerMerge = null;

        if (identity is null || person is null)
        {
            return;
        }

        var ok = await RunAsync(async () =>
        {
            await Task.Run(() => merger.MergeInto(identity.Id, person.Id, confirmOwnerMerge: true))
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (ok)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelOwnerMerge() => PendingOwnerMerge = null;

    [RelayCommand]
    private async Task Unmerge()
    {
        var identity = SelectedIdentity;

        if (identity is null)
        {
            return;
        }

        var ok = await RunAsync(async () =>
        {
            await Task.Run(() => merger.Unmerge(identity.Id)).ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (ok)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
