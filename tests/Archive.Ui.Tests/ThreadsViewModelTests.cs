using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

public sealed class ThreadsViewModelTests
{
    [Fact]
    public async Task Selecting_a_conversation_loads_its_messages_newest_first()
    {
        using var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        Assert.Single(page.Threads);
        Assert.NotNull(page.SelectedThread);
        Assert.Equal(2, page.Messages.Count);
        Assert.Equal("we should go back", page.Messages[0].Plaintext);
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
              "personal_information": { "user_id": 777001, "first_name": "Kirill" },
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
        Assert.Equal(250, page.Messages.Select(m => m.Uid).Distinct().Count());
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
              "personal_information": { "user_id": 777001, "first_name": "Kirill" },
              "chats": { "list": [
                { "name": "Trip", "type": "private_group", "id": 200, "messages": [
                  { "id": 1, "type": "service", "date_unixtime": "1551427200", "actor": "Kirill",
                    "actor_id": "user777001", "action": "create_group", "text": "", "text_entities": [] }
                ] }
              ] }
            }
            """));

        var page = new ThreadsViewModel(save.Queries);
        await page.RefreshAsync();

        var message = Assert.Single(page.Messages);

        Assert.Equal("service", message.Kind);
        Assert.Equal("create_group", message.ServiceAction);
    }
}
