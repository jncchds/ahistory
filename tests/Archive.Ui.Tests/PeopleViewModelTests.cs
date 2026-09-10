using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

/// <summary>
/// The People page: one person at a time, with their accounts inside them.
/// </summary>
/// <remarks>
/// The page used to be two flat lists and a button, and every test here used to describe a pairing
/// of two selections that nothing on screen made visible. What is asserted now is the structure —
/// which accounts belong to whom, and what happens when one is moved.
/// </remarks>
public sealed class PeopleViewModelTests
{
    private static async Task<(TempSave Save, PeopleViewModel Page)> Loaded()
    {
        var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new PeopleViewModel(save.Queries, save.Merger, save.Suggestions);
        await page.RefreshAsync();

        return (save, page);
    }

    /// <summary>Selects the archive's owner.</summary>
    private static void SelectOwner(PeopleViewModel page) =>
        page.SelectedPerson = page.People.First(p => p.IsOwner);

    /// <summary>Selects somebody who is not the owner.</summary>
    private static void SelectContact(PeopleViewModel page) =>
        page.SelectedPerson = page.People.First(p => !p.IsOwner);

    [Fact]
    public async Task People_are_listed_with_the_owner_first_and_their_accounts_counted()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        Assert.Equal(2, page.People.Count);
        Assert.True(page.People[0].IsOwner);
        Assert.All(page.People, p => Assert.Equal(1, p.IdentityCount));
    }

    /// <summary>
    /// Selecting someone shows the accounts that are theirs, and only those.
    /// </summary>
    /// <remarks>
    /// This is the thing the old page could not say. Every account in the archive was listed
    /// together, and which ones were one human had to be read off a line of small print.
    /// </remarks>
    [Fact]
    public async Task Selecting_a_person_shows_their_accounts_and_nobody_elses()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectOwner(page);

        var account = Assert.Single(page.Accounts);

        Assert.Equal("Owner Synthetic", account.Row.DisplayName);
        Assert.Equal("Telegram", account.PlatformLabel);
        Assert.Equal(page.SelectedPerson!.Id, account.Row.PersonId);
    }

    [Fact]
    public async Task Selecting_nobody_leaves_the_detail_pane_empty()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.SelectedPerson = null;

        Assert.False(page.HasSelection);
        Assert.Empty(page.Accounts);
        Assert.Empty(page.Matches);
    }

    /// <summary>An account moves by naming where it should go, not by pairing two selections.</summary>
    [Fact]
    public async Task Moving_an_account_puts_it_on_the_chosen_person()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectContact(page);
        var account = Assert.Single(page.Accounts);
        var owner = page.Accounts[0].MoveTargets.Single(p => p.IsOwner);

        account.MoveTarget = owner;

        // Into the owner, so it asks rather than acting.
        Assert.NotNull(page.PendingOwnerMerge);
        Assert.Contains("becomes something you said", page.PendingOwnerMerge!, StringComparison.Ordinal);

        await page.ConfirmOwnerMergeCommand.ExecuteAsync(null);

        // One person left, holding both accounts.
        Assert.Single(page.People);
        Assert.Equal(2, page.Accounts.Count);
    }

    /// <summary>The dropdown resets, so it never shows a move that has not happened.</summary>
    [Fact]
    public async Task The_move_dropdown_does_not_keep_the_name_it_was_given()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectContact(page);
        var account = page.Accounts[0];

        account.MoveTarget = account.MoveTargets.Single(p => p.IsOwner);

        Assert.Null(account.MoveTarget);
    }

    [Fact]
    public async Task Cancelling_an_owner_merge_leaves_everything_alone()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectContact(page);
        page.Accounts[0].MoveTarget = page.Accounts[0].MoveTargets.Single(p => p.IsOwner);

        page.CancelOwnerMergeCommand.Execute(null);

        Assert.Null(page.PendingOwnerMerge);

        await page.RefreshAsync();
        Assert.Equal(2, page.People.Count);
    }

    /// <summary>Merging is repointing an identity, so undoing it is too (§1).</summary>
    [Fact]
    public async Task An_account_merged_into_the_owner_can_be_detached_again()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectContact(page);
        page.Accounts[0].MoveTarget = page.Accounts[0].MoveTargets.Single(p => p.IsOwner);
        await page.ConfirmOwnerMergeCommand.ExecuteAsync(null);

        Assert.Single(page.People);

        var moved = page.Accounts.Single(a => a.Row.DisplayName == "Sam Ruiz");
        await moved.DetachCommand.ExecuteAsync(null);

        Assert.Equal(2, page.People.Count);
    }

    /// <summary>
    /// The owner's own account came from the export's personal_information, and detaching it
    /// would leave the archive with no definite "me" (P5). The button is not offered at all.
    /// </summary>
    [Fact]
    public async Task The_owners_own_account_offers_no_detach_button()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectOwner(page);

        Assert.True(Assert.Single(page.Accounts).IsSeeded);

        // Every other account can be taken off its person.
        SelectContact(page);
        Assert.False(page.Accounts[0].IsSeeded);
    }

    // Finding people. A merge feature is used by narrowing hundreds of rows to the handful that
    // need attention; a list with no way to narrow it is one nobody finishes going through.

    [Fact]
    public async Task Searching_matches_a_persons_name()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Search = "Sam";
        await page.RefreshAsync();

        Assert.Equal("Sam Ruiz", Assert.Single(page.People).DisplayName);
    }

    /// <summary>
    /// Searching also matches the names of their accounts.
    /// </summary>
    /// <remarks>
    /// Someone hunting a duplicate knows the account name they saw on a message. Which person
    /// currently holds it is the question, so making them guess that first is backwards.
    /// </remarks>
    [Fact]
    public async Task Searching_matches_an_account_name_and_finds_who_holds_it()
    {
        var (save, page) = await LoadedWithADuplicate();
        using var _ = save;

        // Rename the person, leaving the account named as it was.
        page.SelectedPerson = page.People.Single(p => p.Id == "p:vk:222");
        page.NewName = "Someone Else";
        await page.RenameCommand.ExecuteAsync(null);

        page.Search = "Sam Ruiz";
        await page.RefreshAsync();

        Assert.Contains(page.People, p => p.DisplayName == "Someone Else");
    }

    [Fact]
    public async Task The_filter_narrows_to_people_with_something_to_review()
    {
        var (save, page) = await LoadedWithADuplicate();
        using var _ = save;

        Assert.Equal(3, page.People.Count);

        page.Filter = page.Filters.Single(f => f.Kind == PeopleFilterKind.WithPossibleMatches);
        await page.RefreshAsync();

        // The pair, and nobody else.
        Assert.Equal(2, page.People.Count);
        Assert.All(page.People, p => Assert.Equal("Sam Ruiz", p.DisplayName));
    }

    // Suggestions: the app finds the pairs, the user decides. Nothing is applied on its own — an
    // over-merge is a confident lie about who said something (§1).

    /// <summary>A save where the same contact arrives from a second platform under the same name.</summary>
    private static async Task<(TempSave Save, PeopleViewModel Page)> LoadedWithADuplicate()
    {
        var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        save.Execute("""
            INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
            SELECT 'vk:222', 'vk', '222', 'Sam Ruiz', i.first_import_id, i.created_utc
            FROM identity i LIMIT 1;

            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p:vk:222', 'Sam Ruiz', 0, '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
            VALUES ('vk:222', 'p:vk:222', 'auto', '2020-01-01T00:00:00.0000000+00:00');
            """);

        var page = new PeopleViewModel(save.Queries, save.Merger, save.Suggestions);
        await page.RefreshAsync();

        return (save, page);
    }

    /// <summary>A match is shown on the person it concerns, naming the other side.</summary>
    [Fact]
    public async Task A_possible_match_appears_on_both_people_and_names_the_other_one()
    {
        var (save, page) = await LoadedWithADuplicate();
        using var _ = save;

        Assert.Equal(1, page.MatchCount);

        page.SelectedPerson = page.People.Single(p => p.Id == "p:vk:222");
        var fromVk = Assert.Single(page.Matches);

        Assert.Equal("telegram", fromVk.OtherPlatform);

        page.SelectedPerson = page.People.Single(p => p.Id != "p:vk:222" && !p.IsOwner);
        var fromTelegram = Assert.Single(page.Matches);

        Assert.Equal("vk", fromTelegram.OtherPlatform);

        // Offered, not applied.
        Assert.Equal(3, page.People.Count);
    }

    [Fact]
    public async Task Accepting_a_match_puts_both_accounts_on_one_person()
    {
        var (save, page) = await LoadedWithADuplicate();
        using var _ = save;

        page.SelectedPerson = page.People.Single(p => p.Id == "p:vk:222");
        await page.Matches[0].AcceptCommand.ExecuteAsync(null);

        Assert.Equal(0, page.MatchCount);
        Assert.Equal(2, page.People.Count);

        var sam = page.People.Single(p => !p.IsOwner);
        Assert.Equal(2, sam.IdentityCount);
    }

    [Fact]
    public async Task A_rejected_match_does_not_come_back()
    {
        var (save, page) = await LoadedWithADuplicate();
        using var _ = save;

        page.SelectedPerson = page.People.Single(p => p.Id == "p:vk:222");
        await page.Matches[0].RejectCommand.ExecuteAsync(null);

        Assert.Equal(0, page.MatchCount);

        await page.RefreshAsync();

        Assert.Equal(0, page.MatchCount);
        Assert.Equal(3, page.People.Count);
    }

    /// <summary>A match that would make someone you asks first, like any other owner merge.</summary>
    [Fact]
    public async Task A_match_that_touches_the_owner_asks_first()
    {
        var save = new TempSave();
        using var _ = save;

        save.Runner.Run(save.WriteSampleExport());

        save.Execute("""
            INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
            SELECT 'vk:1', 'vk', '1', 'Owner Synthetic', i.first_import_id, i.created_utc
            FROM identity i LIMIT 1;

            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p:vk:1', 'Owner Synthetic', 0, '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
            VALUES ('vk:1', 'p:vk:1', 'auto', '2020-01-01T00:00:00.0000000+00:00');
            """);

        var page = new PeopleViewModel(save.Queries, save.Merger, save.Suggestions);
        await page.RefreshAsync();

        page.SelectedPerson = page.People.Single(p => p.Id == "p:vk:1");
        var match = Assert.Single(page.Matches);

        Assert.True(match.Suggestion.TouchesOwner);

        await match.AcceptCommand.ExecuteAsync(null);

        Assert.NotNull(page.PendingOwnerMerge);
        Assert.Equal(3, page.People.Count);

        await page.ConfirmOwnerMergeCommand.ExecuteAsync(null);

        // The owner survives the merge, and takes the other account with them.
        Assert.Equal(2, page.People.Count);
        Assert.Equal(2, page.People.Single(p => p.IsOwner).IdentityCount);
    }

    /// <summary>
    /// A person merged from several platforms is named after whichever import came first.
    /// </summary>
    [Fact]
    public async Task A_person_can_be_renamed_without_touching_their_accounts()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectContact(page);
        var accountName = page.Accounts[0].Row.DisplayName;

        page.NewName = "Samantha Ruiz";
        await page.RenameCommand.ExecuteAsync(null);

        Assert.Contains(page.People, p => p.DisplayName == "Samantha Ruiz");
        Assert.Equal(accountName, page.Accounts[0].Row.DisplayName);
    }

    /// <summary>Selecting someone loads their current name, so the box never renames the last one.</summary>
    [Fact]
    public async Task The_rename_box_follows_the_selection()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        SelectOwner(page);
        Assert.Equal(page.SelectedPerson!.DisplayName, page.NewName);

        SelectContact(page);
        Assert.Equal(page.SelectedPerson!.DisplayName, page.NewName);
    }

    [Fact]
    public async Task Renaming_with_nothing_selected_does_nothing()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.SelectedPerson = null;
        await page.RenameCommand.ExecuteAsync(null);

        Assert.Null(page.Error);
        Assert.Null(page.PendingOwnerMerge);
        Assert.Equal(2, page.People.Count);
    }
}
