using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

public sealed class SearchViewModelTests
{
    private static async Task<(TempSave Save, SearchViewModel Page)> Loaded()
    {
        var save = new TempSave();
        save.Runner.Run(save.WriteSampleExport());

        var page = new SearchViewModel(save.Queries, save.Search);
        await page.RefreshAsync();

        return (save, page);
    }

    [Fact]
    public async Task Searching_finds_the_message()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Query = "harbour";
        await page.RunCommand.ExecuteAsync(null);

        var hit = Assert.Single(page.Results);

        Assert.Equal("Sam Ruiz", hit.SenderName);
        Assert.Equal(1, page.TotalMatches);
        Assert.False(page.FoundNothing);
    }

    [Fact]
    public async Task The_matched_part_is_marked_for_highlighting()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Query = "harbour";
        await page.RunCommand.ExecuteAsync(null);

        var hit = Assert.Single(page.Results);
        var matched = Assert.Single(hit.Snippet, s => s.IsMatch);

        Assert.Equal("harbour", matched.Text, ignoreCase: true);
    }

    /// <summary>
    /// Finding nothing is a normal outcome, and D8's limitation means it can happen for a reason
    /// the user cannot guess. The page explains rather than showing an empty list.
    /// </summary>
    [Fact]
    public async Task Finding_nothing_offers_an_explanation()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Query = "zeppelin";
        await page.RunCommand.ExecuteAsync(null);

        Assert.Empty(page.Results);
        Assert.True(page.FoundNothing);
        Assert.Contains("start of words", page.EmptyHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_query_is_not_a_search()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Query = "   ";
        await page.RunCommand.ExecuteAsync(null);

        Assert.Empty(page.Results);
        Assert.Equal(0, page.TotalMatches);
        Assert.False(page.FoundNothing);
        Assert.Null(page.Error);
    }

    /// <summary>FTS5 operator syntax must be searchable text, not a syntax error.</summary>
    [Theory]
    [InlineData("NEAR")]
    [InlineData("a AND b")]
    [InlineData("colon:term")]
    [InlineData("\"unclosed")]
    [InlineData("*")]
    public async Task An_awkward_query_does_not_produce_an_error(string query)
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Query = query;
        await page.RunCommand.ExecuteAsync(null);

        Assert.Null(page.Error);
    }

    [Fact]
    public async Task Results_can_be_narrowed_to_one_person()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Query = "should";
        page.SelectedPerson = page.People.First(p => p.Display.Contains("Sam", StringComparison.Ordinal));
        await page.RunCommand.ExecuteAsync(null);

        // "we should go back" was sent by the owner, not by Sam.
        Assert.Empty(page.Results);

        page.SelectedPerson = page.People.First(p => p.Id is null);
        await page.RunCommand.ExecuteAsync(null);

        Assert.Single(page.Results);
    }

    [Fact]
    public async Task The_person_filter_offers_anyone_first()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        Assert.Null(page.People[0].Id);
        Assert.Equal("Anyone", page.People[0].Display);
        Assert.Equal(page.People[0], page.SelectedPerson);
    }

    /// <summary>
    /// An import finishing refreshes every page. A search that kept showing results from before
    /// it would be quietly stale.
    /// </summary>
    [Fact]
    public async Task A_refresh_re_runs_the_current_search()
    {
        var (save, page) = await Loaded();
        using var _ = save;

        page.Query = "months";
        await page.RunCommand.ExecuteAsync(null);
        Assert.Empty(page.Results);

        save.Runner.Run(save.WriteExport("later", """
            {
              "personal_information": { "user_id": 777001, "first_name": "Owner", "last_name": "Synthetic" },
              "chats": { "list": [
                { "name": "Sam Ruiz", "type": "personal_chat", "id": 100, "messages": [
                  { "id": 9, "type": "message", "date_unixtime": "1560000000", "from_id": "user5001",
                    "text": "months later", "text_entities": [ { "type": "plain", "text": "months later" } ] }
                ] }
              ] }
            }
            """));

        await page.RefreshAsync();

        Assert.Single(page.Results);
    }

    /// <summary>
    /// The count is skipped when the results were not truncated, because a short result set has
    /// already counted itself. Both branches have to produce the same number.
    /// </summary>
    [Fact]
    public async Task The_total_is_right_whether_or_not_the_results_were_truncated()
    {
        using var save = new TempSave();

        var messages = string.Join(",", Enumerable.Range(1, 250).Select(i => $$"""
            { "id": {{i}}, "type": "message", "date_unixtime": "{{1_000_000 + i}}", "from_id": "user5001",
              "text": "harbour {{i}}", "text_entities": [ { "type": "plain", "text": "harbour {{i}}" } ] }
            """));

        save.Runner.Run(save.WriteExport("many", $$"""
            {
              "personal_information": { "user_id": 777001, "first_name": "Owner", "last_name": "Synthetic" },
              "chats": { "list": [
                { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [ {{messages}} ] }
              ] }
            }
            """));

        var page = new SearchViewModel(save.Queries, save.Search);
        await page.RefreshAsync();

        // Truncated: the count query runs, and reports everything that matched.
        page.Query = "harbour";
        await page.RunCommand.ExecuteAsync(null);

        Assert.Equal(200, page.Results.Count);
        Assert.Equal(250, page.TotalMatches);
        Assert.True(page.IsTruncated);

        // Not truncated: the count is the result count, with no second pass.
        page.Query = "\"harbour 42\"";
        await page.RunCommand.ExecuteAsync(null);

        Assert.Equal(page.Results.Count, page.TotalMatches);
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public async Task Searching_an_empty_archive_reports_nothing_found()
    {
        using var save = new TempSave();

        var page = new SearchViewModel(save.Queries, save.Search);
        await page.RefreshAsync();

        page.Query = "anything";
        await page.RunCommand.ExecuteAsync(null);

        Assert.Empty(page.Results);
        Assert.True(page.FoundNothing);
        Assert.Null(page.Error);
    }
}
