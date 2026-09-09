using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

public sealed class PeopleViewModelTests
{
    private static async Task<(TempSave Save, PeopleViewModel Page)> Loaded()
    {
        var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new PeopleViewModel(save.Queries, save.Merger);
        await page.RefreshAsync();

        return (save, page);
    }

    [Fact]
    public async Task People_and_their_accounts_are_listed_with_the_owner_first()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        Assert.Equal(2, page.People.Count);
        Assert.True(page.People[0].IsOwner);
        Assert.Equal(2, page.Identities.Count);
    }

    /// <summary>
    /// §1: merging a contact into the owner makes everything they ever said a statement about
    /// you. The command must not perform it — it must ask.
    /// </summary>
    [Fact]
    public async Task Merging_into_the_owner_asks_first_and_changes_nothing_yet()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.SelectedPerson = page.People.First(p => p.IsOwner);
        page.SelectedIdentity = page.Identities.First(i => !i.PersonName.Contains("Kirill", StringComparison.Ordinal));

        var identityId = page.SelectedIdentity.Id;
        var before = page.SelectedIdentity.PersonId;

        await page.MergeCommand.ExecuteAsync(null);

        Assert.NotNull(page.PendingOwnerMerge);
        Assert.Contains("becomes something you said", page.PendingOwnerMerge!, StringComparison.Ordinal);

        await page.RefreshAsync();
        Assert.Equal(before, page.Identities.Single(i => i.Id == identityId).PersonId);
    }

    [Fact]
    public async Task Confirming_the_owner_merge_performs_it()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        var owner = page.People.First(p => p.IsOwner);
        page.SelectedPerson = owner;
        page.SelectedIdentity = page.Identities.First(i => i.PersonId != owner.Id);

        await page.MergeCommand.ExecuteAsync(null);
        await page.ConfirmOwnerMergeCommand.ExecuteAsync(null);

        Assert.Null(page.PendingOwnerMerge);
        Assert.All(page.Identities, i => Assert.Equal(owner.Id, i.PersonId));

        // Both accounts now belong to one person, so there is only one person left.
        Assert.Single(page.People);
    }

    [Fact]
    public async Task Cancelling_the_owner_merge_leaves_everything_alone()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        var owner = page.People.First(p => p.IsOwner);
        page.SelectedPerson = owner;
        page.SelectedIdentity = page.Identities.First(i => i.PersonId != owner.Id);

        await page.MergeCommand.ExecuteAsync(null);
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

        var owner = page.People.First(p => p.IsOwner);
        page.SelectedPerson = owner;
        page.SelectedIdentity = page.Identities.First(i => i.PersonId != owner.Id);
        var identityId = page.SelectedIdentity.Id;

        await page.MergeCommand.ExecuteAsync(null);
        await page.ConfirmOwnerMergeCommand.ExecuteAsync(null);

        page.SelectedIdentity = page.Identities.Single(i => i.Id == identityId);
        await page.UnmergeCommand.ExecuteAsync(null);

        Assert.Equal(2, page.People.Count);
        Assert.NotEqual(owner.Id, page.Identities.Single(i => i.Id == identityId).PersonId);
    }

    /// <summary>
    /// The owner's own account came from the export's personal_information. Detaching it would
    /// leave the archive with no definite "me" (P5) — the page reports that rather than throwing.
    /// </summary>
    [Fact]
    public async Task Detaching_the_owners_own_account_is_refused_with_an_explanation()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        var owner = page.People.First(p => p.IsOwner);
        page.SelectedIdentity = page.Identities.First(i => i.PersonId == owner.Id);

        await page.UnmergeCommand.ExecuteAsync(null);

        Assert.NotNull(page.Error);
        Assert.Contains("owner", page.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, page.People.Count);
    }

    [Fact]
    public async Task Merging_with_nothing_selected_does_nothing()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        await page.MergeCommand.ExecuteAsync(null);
        await page.UnmergeCommand.ExecuteAsync(null);

        Assert.Null(page.Error);
        Assert.Null(page.PendingOwnerMerge);
        Assert.Equal(2, page.People.Count);
    }
}
