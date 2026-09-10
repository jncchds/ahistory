using Archive.Import.Synthetic;
using Archive.Ui.Services;
using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

/// <summary>
/// The import page, tested without a window. Everything here is view-model logic — which source
/// the export belongs to, what the user is warned about, whether the archive reloads afterwards.
/// </summary>
public sealed class ImportViewModelTests
{
    [Fact]
    public async Task Browsing_to_a_folder_loads_a_preview_and_offers_a_source()
    {
        using var save = new TempSave();
        var picker = new FakeFolderPicker(save.WriteSampleExport());
        var page = new ImportViewModel(save.Runner, picker);

        await page.BrowseCommand.ExecuteAsync(null);

        Assert.NotNull(page.Preview);
        Assert.Equal("777001", page.Preview!.DetectedAccountId);
        Assert.True(page.CanImport);

        var choice = Assert.Single(page.SourceChoices);
        Assert.True(choice.IsNew);
        Assert.Equal(choice, page.SelectedSource);
    }

    [Fact]
    public async Task Cancelling_the_folder_dialog_changes_nothing()
    {
        using var save = new TempSave();
        var page = new ImportViewModel(save.Runner, new FakeFolderPicker(null));

        await page.BrowseCommand.ExecuteAsync(null);

        Assert.Null(page.ExportFolder);
        Assert.Null(page.Preview);
        Assert.False(page.CanImport);
    }

    [Fact]
    public async Task Importing_reports_what_it_did()
    {
        using var save = new TempSave();
        var page = new ImportViewModel(save.Runner, new FakeFolderPicker(save.WriteSampleExport()));

        await page.BrowseCommand.ExecuteAsync(null);
        await page.ImportCommand.ExecuteAsync(null);

        Assert.Null(page.Error);
        Assert.NotNull(page.Result);
        Assert.Equal(2, page.Result!.MessagesInserted);
        Assert.False(page.IsImporting);
    }

    /// <summary>
    /// Every other page shows numbers derived from the archive, so an import that did not tell
    /// them would leave the overview reporting the counts from before it ran.
    /// </summary>
    [Fact]
    public async Task A_finished_import_notifies_the_rest_of_the_app()
    {
        using var save = new TempSave();
        var page = new ImportViewModel(save.Runner, new FakeFolderPicker(save.WriteSampleExport()));

        var notified = 0;
        page.Imported += () => { notified++; return Task.CompletedTask; };

        await page.BrowseCommand.ExecuteAsync(null);
        await page.ImportCommand.ExecuteAsync(null);

        Assert.Equal(1, notified);
    }

    /// <summary>
    /// The second import of an account already in the archive extends it rather than starting a
    /// new one — and the page says so, because "which source" is a question the user answers.
    /// </summary>
    [Fact]
    public async Task A_second_export_of_the_same_account_offers_the_existing_source()
    {
        using var save = new TempSave();
        var folder = save.WriteSampleExport();
        var page = new ImportViewModel(save.Runner, new FakeFolderPicker(folder));

        await page.BrowseCommand.ExecuteAsync(null);
        await page.ImportCommand.ExecuteAsync(null);
        await page.BrowseCommand.ExecuteAsync(null);

        var choice = Assert.Single(page.SourceChoices);

        Assert.False(choice.IsNew);
        Assert.True(choice.IsSuggested);
        Assert.Contains("2 messages", choice.Display, StringComparison.Ordinal);
        Assert.Contains("same account", page.Preview!.SuggestionReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P5: a save is one person's archive, and the importer cannot tell a second account of yours
    /// from an archive someone handed you. It has to say so before merging it into your identity.
    /// </summary>
    [Fact]
    public async Task An_export_from_another_account_raises_a_visible_warning()
    {
        using var save = new TempSave();
        var mine = new ImportViewModel(save.Runner, new FakeFolderPicker(save.WriteSampleExport()));

        await mine.BrowseCommand.ExecuteAsync(null);
        await mine.ImportCommand.ExecuteAsync(null);

        Assert.False(mine.ShowsAccountWarning);

        var stranger = save.WriteExport("stranger", """
            {
              "personal_information": { "user_id": 999003, "first_name": "Sam", "last_name": "Ruiz" },
              "chats": { "list": [
                { "name": "Alex", "type": "personal_chat", "id": 5002, "messages": [
                  { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5002",
                    "text": "hello", "text_entities": [ { "type": "plain", "text": "hello" } ] }
                ] }
              ] }
            }
            """);

        var page = new ImportViewModel(save.Runner, new FakeFolderPicker(stranger));
        await page.BrowseCommand.ExecuteAsync(null);

        Assert.True(page.ShowsAccountWarning);
        Assert.Contains("Sam Ruiz", page.AccountWarning, StringComparison.Ordinal);
        Assert.Contains("separate save", page.AccountWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_user_can_send_an_export_to_a_different_source()
    {
        using var save = new TempSave();
        var page = new ImportViewModel(save.Runner, new FakeFolderPicker(save.WriteSampleExport()));

        await page.BrowseCommand.ExecuteAsync(null);
        await page.ImportCommand.ExecuteAsync(null);

        // Offer it again, this time deliberately as its own source.
        await page.BrowseCommand.ExecuteAsync(null);
        page.SelectedSource = new SourceChoice("telegram:account:elsewhere", "Elsewhere", IsNew: true, IsSuggested: false);
        await page.ImportCommand.ExecuteAsync(null);

        var summary = save.Queries.Summary();

        Assert.Equal(2, summary.Sources);
        Assert.Equal(2, summary.Messages);
    }

    [Fact]
    public async Task A_folder_that_is_not_an_export_fails_without_taking_the_page_down()
    {
        using var save = new TempSave();
        var empty = save.WriteExport("empty", "{}");
        File.Delete(Path.Combine(empty, "result.json"));

        var page = new ImportViewModel(save.Runner, new FakeFolderPicker(empty));

        await page.BrowseCommand.ExecuteAsync(null);

        Assert.NotNull(page.Error);
        Assert.Contains("does not look like an export", page.Error!, StringComparison.Ordinal);
        Assert.False(page.IsBusy);
    }

    /// <summary>
    /// A format that will not name its account gets asked about rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Inventing an owner is what left a user's real account arriving as an ordinary contact, and
    /// a history file for their own account reading as a conversation with themselves.
    /// </remarks>
    [Fact]
    public async Task An_export_that_does_not_name_its_account_is_asked_about()
    {
        using var save = new TempSave();

        var page = new ImportViewModel(
            save.Runner,
            new FakeFolderPicker(VkExportBuilder.New()
                .Conversation("222", "Sam Ruiz", c => c
                    .Message(1, DateTimeOffset.FromUnixTimeSeconds(1577882096), VkAuthor.You, "hello"))
                .Write(save.ExportFolder("vk"))));

        await page.BrowseCommand.ExecuteAsync(null);

        Assert.True(page.AsksForAccount);
        Assert.Contains("VKontakte", page.AccountQuestion, StringComparison.Ordinal);
    }

    /// <summary>A Telegram export states its account, so nothing is asked.</summary>
    [Fact]
    public async Task An_export_that_names_its_account_is_not_asked_about()
    {
        using var save = new TempSave();

        var page = new ImportViewModel(
            save.Runner, new FakeFolderPicker(save.WriteSampleExport()));

        await page.BrowseCommand.ExecuteAsync(null);

        Assert.False(page.AsksForAccount);
    }

    /// <summary>What the user types is what the owner becomes.</summary>
    [Fact]
    public async Task The_account_the_user_gives_is_used_for_the_import()
    {
        using var save = new TempSave();

        var page = new ImportViewModel(
            save.Runner,
            new FakeFolderPicker(VkExportBuilder.New()
                .Conversation("222", "Sam Ruiz", c => c
                    .Message(1, DateTimeOffset.FromUnixTimeSeconds(1577882096), VkAuthor.You, "hello"))
                .Write(save.ExportFolder("vk"))));

        await page.BrowseCommand.ExecuteAsync(null);

        page.OwnerAccountId = "999";
        await page.ImportCommand.ExecuteAsync(null);

        Assert.Null(page.Error);

        var owner = save.Queries.Identities()
            .Single(i => i.Platform == "vk");

        Assert.Equal("999", owner.SourceIdentityId);
        Assert.False(owner.IsSynthetic);
    }

    private sealed class FakeFolderPicker(string? folder) : IFolderPicker
    {
        public Task<string?> PickAsync(string title) => Task.FromResult(folder);
    }
}
