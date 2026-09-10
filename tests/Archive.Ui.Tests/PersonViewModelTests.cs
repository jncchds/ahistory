using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

/// <summary>
/// The centrepiece (§4): one person, everything they said to you and in groups, in one stream.
/// </summary>
public sealed class PersonViewModelTests
{
    /// <summary>An export with a direct thread and a group the same person also speaks in.</summary>
    private static string Export(string messages) => $$"""
        {
          "personal_information": { "user_id": 777001, "first_name": "Owner", "last_name": "Synthetic" },
          "chats": { "list": [ {{messages}} ] }
        }
        """;

    private static string DirectThread(params string[] messages) => $$"""
        { "name": "Sam Ruiz", "type": "personal_chat", "id": 100, "messages": [ {{string.Join(",", messages)}} ] }
        """;

    private static string Group(params string[] messages) => $$"""
        { "name": "Prague trip", "type": "private_group", "id": 200, "messages": [ {{string.Join(",", messages)}} ] }
        """;

    private static string Message(long id, long unix, string from, string text) => $$"""
        { "id": {{id}}, "type": "message", "date_unixtime": "{{unix}}", "from_id": "{{from}}",
          "text": "{{text}}", "text_entities": [ { "type": "plain", "text": "{{text}}" } ] }
        """;

    private static async Task<(TempSave Save, PersonViewModel Page)> Loaded(string exportJson)
    {
        var save = new TempSave();
        save.Runner.Run(save.WriteExport("export", exportJson));

        var page = new PersonViewModel(save.Queries, save.Conversation);
        await page.RefreshAsync();

        return (save, page);
    }

    private static IEnumerable<MessageItem> MessagesOf(PersonViewModel page) =>
        page.Items.OfType<MessageItem>();

    [Fact]
    public async Task A_conversation_opens_on_someone_other_than_you()
    {
        var (save, page) = await Loaded(Export(DirectThread(
            Message(1, 1000, "user5001", "theirs"),
            Message(2, 1001, "user777001", "mine"))));

        using var _ = save;

        Assert.NotNull(page.SelectedPerson);
        Assert.False(page.SelectedPerson!.IsOwner);
        Assert.Equal(2, MessagesOf(page).Count());
    }

    [Fact]
    public async Task Direct_and_group_messages_appear_in_one_stream()
    {
        var (save, page) = await Loaded(Export(
            DirectThread(Message(1, 1000, "user5001", "in the dm")) + "," +
            Group(
                Message(10, 2000, "user5001", "in the group"),
                Message(11, 2001, "user777001", "my group line"))));

        using var _ = save;

        var texts = MessagesOf(page).Select(m => m.Row.Plaintext).ToArray();

        // Their group line is included; mine is not — this is their conversation. Oldest first,
        // the way a conversation reads.
        Assert.Equal(["in the dm", "in the group"], texts);
    }

    /// <summary>
    /// Pages are fetched newest-first, because that is what makes opening a ten-year conversation
    /// instant. They are read oldest-first, because that is what makes it a conversation rather
    /// than a log. Loading older pages must not disturb that.
    /// </summary>
    [Fact]
    public async Task The_conversation_reads_oldest_first_however_many_pages_are_loaded()
    {
        var messages = Enumerable.Range(1, 250)
            .Select(i => Message(i, 1_000_000 + i, "user5001", $"message {i}"))
            .ToArray();

        var (save, page) = await Loaded(Export(DirectThread(messages)));
        using var _ = save;

        while (page.HasMore)
        {
            await page.LoadMoreCommand.ExecuteAsync(null);
        }

        var times = MessagesOf(page).Select(m => m.Row.SentAtUnix).ToArray();

        Assert.Equal(250, times.Length);
        Assert.Equal(times.OrderBy(t => t), times);
        Assert.Equal("message 1", MessagesOf(page).First().Row.Plaintext);
        Assert.Equal("message 250", MessagesOf(page).Last().Row.Plaintext);
    }

    /// <summary>§4: a group line is marked, and carries the room it was said in.</summary>
    [Fact]
    public async Task A_group_line_is_marked_and_can_be_expanded()
    {
        var (save, page) = await Loaded(Export(
            DirectThread(Message(1, 1000, "user5001", "in the dm")) + "," +
            Group(Message(10, 2000, "user5001", "yeah exactly"))));

        using var _ = save;

        var group = Assert.Single(MessagesOf(page), m => m.Row.IsFromGroup);
        var direct = Assert.Single(MessagesOf(page), m => !m.Row.IsFromGroup);

        Assert.Equal("Prague trip", group.Row.ThreadTitle);
        Assert.True(group.CanExpand);
        Assert.False(direct.CanExpand);
    }

    /// <summary>
    /// §4: "yeah exactly" is nonsense alone. Expanding fetches what it was answering — and only
    /// then, because most group lines are never expanded.
    /// </summary>
    [Fact]
    public async Task Expanding_a_group_line_loads_the_conversation_around_it()
    {
        var (save, page) = await Loaded(Export(Group(
            Message(10, 2000, "user777001", "shall we take the 07:40?"),
            Message(11, 2001, "user5001", "yeah exactly"),
            Message(12, 2002, "user777001", "booked"))));

        using var _ = save;

        var group = Assert.Single(MessagesOf(page));

        Assert.Empty(group.Context);

        await group.ToggleContextCommand.ExecuteAsync(null);

        Assert.True(group.IsExpanded);
        Assert.Equal(3, group.Context.Count);
        Assert.Equal("shall we take the 07:40?", group.Context[0].Plaintext);

        // Toggling again collapses without re-fetching.
        await group.ToggleContextCommand.ExecuteAsync(null);

        Assert.False(group.IsExpanded);
        Assert.Equal(3, group.Context.Count);
    }

    /// <summary>
    /// §8: "four months, no contact" is often the most meaningful thing in a timeline, and a view
    /// that only draws messages papers straight over it.
    /// </summary>
    [Fact]
    public async Task A_long_gap_becomes_a_visible_stretch_of_silence()
    {
        // Two messages a hundred days apart.
        var (save, page) = await Loaded(Export(DirectThread(
            Message(1, 1_000_000, "user5001", "before"),
            Message(2, 1_000_000 + (100 * 86_400), "user5001", "after"))));

        using var _ = save;

        var silence = Assert.Single(page.Items.OfType<SilenceItem>());

        Assert.Equal(100, (int)silence.Gap.TotalDays);
        Assert.Contains("months, no contact", silence.Text, StringComparison.Ordinal);

        // It sits between the two messages, not at either end.
        Assert.Equal(1, page.Items.IndexOf(silence));
    }

    [Fact]
    public async Task Short_gaps_are_not_drawn()
    {
        var (save, page) = await Loaded(Export(DirectThread(
            Message(1, 1_000_000, "user5001", "before"),
            Message(2, 1_000_000 + 3600, "user5001", "an hour later"))));

        using var _ = save;

        Assert.Empty(page.Items.OfType<SilenceItem>());
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task Older_messages_load_without_repeating_any()
    {
        var messages = Enumerable.Range(1, 250)
            .Select(i => Message(i, 1_000_000 + i, "user5001", $"message {i}"))
            .ToArray();

        var (save, page) = await Loaded(Export(DirectThread(messages)));
        using var _ = save;

        Assert.Equal(100, MessagesOf(page).Count());
        Assert.True(page.HasMore);

        while (page.HasMore)
        {
            await page.LoadMoreCommand.ExecuteAsync(null);
        }

        var uids = MessagesOf(page).Select(m => m.Row.Uid).ToArray();

        Assert.Equal(250, uids.Length);
        Assert.Equal(250, uids.Distinct().Count());
    }

    [Fact]
    public async Task Switching_person_replaces_the_stream()
    {
        var (save, page) = await Loaded(Export(
            DirectThread(Message(1, 1000, "user5001", "from sam")) + "," + $$"""
            { "name": "Alex", "type": "personal_chat", "id": 300, "messages": [
              {{Message(1, 2000, "user5002", "from alex")}} ] }
            """));

        using var _ = save;

        var first = page.SelectedPerson!;
        Assert.Contains(MessagesOf(page), m => m.Row.Plaintext.Contains("sam", StringComparison.Ordinal)
                                            || m.Row.Plaintext.Contains("alex", StringComparison.Ordinal));

        page.SelectedPerson = page.People.First(p => p.Id != first.Id && !p.IsOwner);
        await page.RefreshAsync();

        Assert.Single(MessagesOf(page));
    }

    [Fact]
    public async Task An_empty_archive_shows_nothing_and_no_error()
    {
        using var save = new TempSave();

        var page = new PersonViewModel(save.Queries, save.Conversation);
        await page.RefreshAsync();

        Assert.Empty(page.People);
        Assert.Empty(page.Items);
        Assert.Null(page.Error);
        Assert.False(page.HasMore);
    }

    /// <summary>
    /// Opening a search result lands in the middle of the conversation, on the message itself.
    /// </summary>
    /// <remarks>
    /// The point of anchoring rather than paging backwards from the present: a hit two hundred
    /// messages deep has to cost the same as opening the conversation does.
    /// </remarks>
    [Fact]
    public async Task Opening_a_result_positions_the_stream_on_that_message()
    {
        var messages = Enumerable.Range(1, 250)
            .Select(i => Message(i, 1_000_000 + i, "user5001", $"message {i}"))
            .ToArray();

        var (save, page) = await Loaded(Export(DirectThread(messages)));
        using var _ = save;

        var person = page.SelectedPerson!;
        var target = save.Conversation.Page(person.Id, 300).Messages
            .Single(m => m.Plaintext == "message 5");

        await page.RevealAsync(person.Id, target.Id);

        var revealed = Assert.Single(MessagesOf(page), m => m.IsRevealed);

        Assert.Equal("message 5", revealed.Row.Plaintext);

        // Everything from the start of the conversation up to it, and a page on from it: landing
        // on a message and being unable to read what came next is the half nobody wants.
        Assert.Equal("message 1", MessagesOf(page).First().Row.Plaintext);
        Assert.Contains(MessagesOf(page), m => m.Row.Plaintext == "message 6");
        Assert.True(page.HasNewer);
        Assert.False(page.HasMore);
    }

    /// <summary>
    /// What the view calls as the reader reaches the bottom of a stream entered from the middle.
    /// </summary>
    [Fact]
    public async Task Reading_on_from_a_revealed_message_reaches_the_end()
    {
        var messages = Enumerable.Range(1, 250)
            .Select(i => Message(i, 1_000_000 + i, "user5001", $"message {i}"))
            .ToArray();

        var (save, page) = await Loaded(Export(DirectThread(messages)));
        using var _ = save;

        var person = page.SelectedPerson!;
        var target = save.Conversation.Page(person.Id, 300).Messages
            .Single(m => m.Plaintext == "message 5");

        await page.RevealAsync(person.Id, target.Id);

        while (page.HasNewer)
        {
            await page.LoadNewerCommand.ExecuteAsync(null);
        }

        var texts = MessagesOf(page).Select(m => m.Row.Plaintext).ToArray();

        Assert.Equal(250, texts.Length);
        Assert.Equal(250, texts.Distinct().Count());
        Assert.Equal("message 250", texts[^1]);
    }

    /// <summary>A conversation still opens at its newest end when nobody asked for a message.</summary>
    [Fact]
    public async Task Opening_a_conversation_normally_has_nothing_below_it()
    {
        var messages = Enumerable.Range(1, 250)
            .Select(i => Message(i, 1_000_000 + i, "user5001", $"message {i}"))
            .ToArray();

        var (save, page) = await Loaded(Export(DirectThread(messages)));
        using var _ = save;

        Assert.False(page.HasNewer);
        Assert.True(page.HasMore);
        Assert.Equal("message 250", MessagesOf(page).Last().Row.Plaintext);
    }
}
