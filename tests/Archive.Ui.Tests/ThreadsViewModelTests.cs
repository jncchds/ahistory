using Archive.Import.Synthetic;
using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

public sealed class ThreadsViewModelTests
{
    [Fact]
    public async Task Selecting_a_conversation_loads_its_messages_oldest_first()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.Single(page.Threads);
        Assert.NotNull(page.SelectedThread);
        Assert.Equal(2, page.Messages.Count);
        Assert.Equal("we should go back", page.Messages[^1].Row.Plaintext);
    }

    [Fact]
    public async Task An_empty_archive_shows_no_conversations_and_no_error()
    {
        using var save = new TempSave();

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.Empty(page.Threads);
        Assert.Empty(page.Messages);
        Assert.Null(page.Error);
        Assert.False(page.HasMore);
    }

    /// <summary>
    /// Paging is keyset, so scrolling back stays constant-time and a background import inserting
    /// underneath cannot shift the boundaries.
    /// </summary>
    [Fact]
    public async Task Older_messages_load_without_repeating_or_skipping_any()
    {
        using var save = new TempSave();

        var messages = string.Join(",\n", Enumerable.Range(1, 250).Select(i =>
            $$"""
              { "id": {{i}}, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
                "text": "message {{i}}", "text_entities": [ { "type": "plain", "text": "message {{i}}" } ] }
              """));

        save.Runner.Run(save.WriteExport("many", $$"""
            {
              "personal_information": { "user_id": 777001, "first_name": "Owner", "last_name": "Synthetic" },
              "chats": { "list": [
                { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [ {{messages}} ] }
              ] }
            }
            """));

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.Equal(100, page.Messages.Count);
        Assert.True(page.HasMore);

        while (page.HasMore)
        {
            await page.LoadMoreCommand.ExecuteAsync(null);
        }

        Assert.Equal(250, page.Messages.Count);
        Assert.Equal(250, page.Messages.Select(m => m.Row.Uid).Distinct().Count());
    }

    /// <summary>
    /// An import finishing refreshes every page. That must not bounce the reader out of whatever
    /// they were reading.
    /// </summary>
    [Fact]
    public async Task A_refresh_keeps_the_reader_in_the_same_conversation()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        var chosen = page.SelectedThread!.Id;

        await page.RefreshAsync();

        Assert.Equal(chosen, page.SelectedThread!.Id);
    }

    [Fact]
    public async Task Service_messages_keep_their_action_so_they_can_be_shown_as_events()
    {
        using var save = new TempSave();

        save.Runner.Run(save.WriteExport("service", """
            {
              "personal_information": { "user_id": 777001, "first_name": "Owner", "last_name": "Synthetic" },
              "chats": { "list": [
                { "name": "Trip", "type": "private_group", "id": 200, "messages": [
                  { "id": 1, "type": "service", "date_unixtime": "1551427200", "actor": "Owner Synthetic",
                    "actor_id": "user777001", "action": "create_group", "text": "", "text_entities": [] }
                ] }
              ] }
            }
            """));

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        var message = Assert.Single(page.Messages);

        Assert.Equal("service", message.Row.Kind);
        Assert.Equal("create_group", message.Row.ServiceAction);
    }

    // A group conversation is only readable here — the per-person stream carries the messages one
    // person sent in a group, never the group itself.

    /// <summary>
    /// In a group, each speaker's run is labelled once.
    /// </summary>
    /// <remarks>
    /// Labelling every bubble puts a column of the same name down the screen; labelling none makes
    /// five speakers indistinguishable, which is what the page used to do.
    /// </remarks>
    [Fact]
    public async Task A_group_labels_the_first_message_of_each_run_and_not_the_rest()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteGroupExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        var labelled = page.Messages
            .Select(m => (m.Row.Plaintext, m.ShowsSender))
            .ToArray();

        Assert.Equal(
            [
                ("yeah exactly", true),
                ("the early train then", false),
                ("поезд в 07:40", true),
                // Yours is on the other side of the screen and needs no name on it.
                ("booked", false),
                ("see you thursday", true),
            ],
            labelled);
    }

    /// <summary>A direct conversation names nobody: every incoming message is the same person.</summary>
    [Fact]
    public async Task A_direct_conversation_labels_nothing()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.All(page.Messages, m => Assert.False(m.ShowsSender));
    }

    [Fact]
    public async Task A_group_lists_who_is_in_it()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteGroupExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.Equal(
            ["Alex Novak", "Owner Synthetic", "Sam Ruiz", "Марина Коваль"],
            page.Participants.Select(p => p.DisplayName).Order());

        Assert.Single(page.Participants, p => p.IsOwner);

        // Sam said two of the five, which is what makes the roster more than a name list.
        Assert.Equal(2, page.Participants.Single(p => p.DisplayName == "Sam Ruiz").MessageCount);
    }

    [Fact]
    public async Task The_conversation_list_can_be_filtered()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());
        save.Runner.Run(save.WriteGroupExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.Equal(2, page.Threads.Count);

        page.ThreadFilter = "Prague";
        await page.RefreshAsync();

        Assert.Equal("Prague trip", Assert.Single(page.Threads).DisplayTitle);
    }

    /// <summary>
    /// An untitled group is named after who is in it rather than rendering as a blank row.
    /// </summary>
    [Fact]
    public async Task An_untitled_conversation_falls_back_to_its_participants()
    {
        using var save = new TempSave();

        save.Runner.Run(save.WriteExport(
            "untitled",
            TelegramExportBuilder.Full()
                .Owner(777001, "Owner", "Synthetic")
                .Chat(name: null, "private_group", 700, c => c
                    .Message(1, DateTimeOffset.FromUnixTimeSeconds(1551427200), TempSave.Sam, "hello"))));

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        var thread = Assert.Single(page.Threads);

        Assert.Null(thread.Title);
        Assert.Equal("Sam Ruiz", thread.DisplayTitle);
    }

    /// <summary>Kinds are shown in words; "dm" is a column name, not a label.</summary>
    [Fact]
    public async Task A_conversations_kind_is_shown_in_words()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.Equal("Direct", Assert.Single(page.Threads).KindLabel);
    }

    /// <summary>
    /// Loading an older page does not leave a stray label at the seam.
    /// </summary>
    /// <remarks>
    /// The first message of a page has nothing before it, so it always looks like the start of a
    /// run — until the page above it arrives and the one underneath turns out to be by the same
    /// person. Recomputing that one item is what keeps a run unbroken across the boundary.
    /// </remarks>
    [Fact]
    public async Task A_run_that_spans_a_page_boundary_is_labelled_once()
    {
        using var save = new TempSave();

        var export = TelegramExportBuilder.Full()
            .Owner(777001, "Owner", "Synthetic")
            .Chat("Prague trip", "private_group", 200, c =>
            {
                // 150 in a row from one person, so a page boundary lands in the middle of a run.
                for (var i = 1; i <= 150; i++)
                {
                    c.Message(i, DateTimeOffset.FromUnixTimeSeconds(1551427200 + i), TempSave.Sam, $"message {i}");
                }
            });

        save.Runner.Run(save.WriteExport("long-run", export));

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.True(page.HasMore);
        await page.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(150, page.Messages.Count);
        Assert.Equal(1, page.Messages.Count(m => m.ShowsSender));
        Assert.True(page.Messages[0].ShowsSender);
    }
}
