namespace Archive.Data.Tests;

/// <summary>
/// §5, and AGENTS.md P1: keyword search is the baseline capability and works with no AI component
/// present at all.
/// </summary>
public sealed class SearchTests
{
    private static (TempDatabase Db, ArchiveSearch Search) Fixture()
    {
        var db = new TempDatabase();
        Seed.Basics(db);
        Seed.People(db);

        return (db, new ArchiveSearch(db.Database));
    }

    [Fact]
    public void A_word_finds_the_message_that_contains_it()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "the harbour was freezing");
        Seed.Message(db, "tg/100/2", "we should go back");

        var hit = Assert.Single(search.Search("harbour"));

        Assert.Equal("tg/100/1", hit.Uid);
        Assert.Equal("message", hit.Provenance);
        Assert.Equal("Sam", hit.SenderName);
    }

    [Fact]
    public void Several_words_must_all_appear()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "the harbour was freezing");
        Seed.Message(db, "tg/100/2", "the harbour in summer");

        Assert.Single(search.Search("harbour freezing"));
        Assert.Equal(2, search.Search("harbour").Count);
    }

    [Fact]
    public void A_quoted_phrase_matches_only_that_order()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "the harbour was freezing");
        Seed.Message(db, "tg/100/2", "freezing at the harbour");

        Assert.Single(search.Search("\"harbour was freezing\""));
        Assert.Equal(2, search.Search("harbour freezing").Count);
    }

    /// <summary>
    /// What prefix expansion actually does, and does not do (decisions.md D8).
    /// </summary>
    /// <remarks>
    /// It matches words <em>starting with</em> what was typed. So a stem finds every inflected
    /// form built on it — but a full inflected form does not find a different one, because
    /// <c>Прага</c> and <c>Праге</c> diverge at the last character. Reaching one inflection from
    /// another needs a lemmatizer, which FTS5 has for no Slavic language.
    ///
    /// This is a real limitation of the search, not an implementation gap, and the search view
    /// says so rather than leaving the user to conclude the archive is empty.
    /// </remarks>
    [Fact]
    public void A_stem_reaches_every_form_built_on_it()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "мы были в Праге весной");
        Seed.Message(db, "tg/100/2", "Прага была холодной");

        // The stem finds both inflections.
        Assert.Equal(2, search.Search("Праг").Count);

        // A full form finds only itself — prefix expansion is not stemming.
        Assert.Single(search.Search("Праге"));
        Assert.Single(search.Search("Прага"));

        // Without expansion, even the stem matches nothing: it is not a word in either message.
        Assert.Empty(search.Search("Праг", expandPrefixes: false));
    }

    [Fact]
    public void A_partial_word_finds_the_whole_one()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "the harbour was freezing");

        Assert.Single(search.Search("harb"));
        Assert.Empty(search.Search("harb", expandPrefixes: false));
    }

    [Fact]
    public void Cyrillic_matches_whatever_case_it_was_typed_in()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "Прага была холодной");

        Assert.Single(search.Search("прага"));
        Assert.Single(search.Search("ПРАГА"));
    }

    /// <summary>
    /// FTS5 has its own query syntax, so a search for one of its keywords — or for a name with a
    /// colon in it — would be a syntax error rather than a search.
    /// </summary>
    [Theory]
    [InlineData("NEAR")]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("NOT")]
    [InlineData("^caret")]
    [InlineData("colon:term")]
    [InlineData("star*")]
    [InlineData("(parens)")]
    [InlineData("quote\"inside")]
    [InlineData("-minus")]
    public void Fts_operator_syntax_is_treated_as_text(string term)
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "an ordinary message");

        // The point is that it does not throw. Whether it matches is beside it.
        var results = search.Search(term);

        Assert.NotNull(results);
    }

    [Fact]
    public void A_term_containing_a_quote_is_searchable()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "he said \"maybe\" and left");

        Assert.Single(search.Search("maybe"));
    }

    [Fact]
    public void An_all_punctuation_query_returns_nothing_rather_than_failing()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "an ordinary message");

        Assert.Empty(search.Search("!!! ??? ..."));
        Assert.Empty(search.Search("   "));
        Assert.Empty(search.Search(null));
    }

    [Fact]
    public void The_snippet_marks_which_part_matched()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "the harbour was freezing");

        var hit = Assert.Single(search.Search("harbour"));
        var matched = Assert.Single(hit.Snippet, s => s.IsMatch);

        Assert.Equal("harbour", matched.Text, ignoreCase: true);
        Assert.Contains(hit.Snippet, s => !s.IsMatch);

        // The snippet still reads as the original text once the markers are stripped.
        Assert.Equal("the harbour was freezing", string.Concat(hit.Snippet.Select(s => s.Text)));
    }

    /// <summary>
    /// The parser degrades rather than throwing. A search box that fails on an unusual message is
    /// worse than one that renders it without highlighting.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("plain text with no markers")]
    public void Snippet_parsing_survives_text_without_markers(string snippet)
    {
        var segments = ArchiveSearch.ParseSnippet(snippet);

        Assert.All(segments, s => Assert.False(s.IsMatch));
        Assert.Equal(snippet, string.Concat(segments.Select(s => s.Text)));
    }

    [Fact]
    public void Snippet_parsing_survives_an_unclosed_marker()
    {
        var segments = ArchiveSearch.ParseSnippet("before " + (char)2 + "after");

        Assert.Equal("before after", string.Concat(segments.Select(s => s.Text)));
        Assert.Contains(segments, s => s.IsMatch && s.Text == "after");
    }

    [Fact]
    public void Results_can_be_narrowed_to_one_conversation()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "harbour in the dm");
        Seed.Message(db, "tg/200/1", "harbour in the group", Seed.OtherThreadId);

        Assert.Equal(2, search.Search("harbour").Count);
        Assert.Single(search.Search("harbour", new SearchFilter(ThreadId: Seed.ThreadId)));
    }

    [Fact]
    public void Results_can_be_narrowed_to_one_person()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/100/1", "harbour from sam", Seed.IdentityId, 1000);
        Seed.MessageFrom(db, "tg/100/2", "harbour from me", Seed.OwnerIdentityId, 1001);

        var mine = Assert.Single(search.Search("harbour", new SearchFilter(PersonId: Seed.OwnerPersonId)));

        Assert.True(mine.FromOwner);
        Assert.Equal("harbour from me", string.Concat(mine.Snippet.Select(s => s.Text)));
    }

    [Fact]
    public void Results_can_be_narrowed_to_a_date_range()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/100/1", "harbour early", Seed.IdentityId, 1000);
        Seed.MessageFrom(db, "tg/100/2", "harbour late", Seed.IdentityId, 9000);

        Assert.Single(search.Search("harbour", new SearchFilter(FromUnix: 5000)));
        Assert.Single(search.Search("harbour", new SearchFilter(ToUnix: 5000)));
        Assert.Equal(2, search.Search("harbour", new SearchFilter(FromUnix: 0, ToUnix: 10_000)).Count);
    }

    [Fact]
    public void The_count_ignores_the_result_limit()
    {
        var (db, search) = Fixture();
        using var _ = db;

        for (var i = 1; i <= 20; i++)
        {
            Seed.Message(db, $"tg/100/{i}", $"harbour {i}");
        }

        Assert.Equal(5, search.Search("harbour", limit: 5).Count);

        var (count, isExact) = search.Count("harbour");

        Assert.Equal(20, count);
        Assert.True(isExact);
    }

    /// <summary>
    /// A word in a large share of the archive costs a full second pass to count exactly, for a
    /// number nobody reads precisely. Past the cap it stops and says so.
    /// </summary>
    [Fact]
    public void The_count_stops_at_the_cap_and_reports_that_it_did()
    {
        var (db, search) = Fixture();
        using var _ = db;

        for (var i = 1; i <= 50; i++)
        {
            Seed.Message(db, $"tg/100/{i}", $"harbour {i}");
        }

        var (capped, isExact) = search.Count("harbour", cap: 10);

        Assert.Equal(10, capped);
        Assert.False(isExact);

        var (exact, wasExact) = search.Count("harbour", cap: 1000);

        Assert.Equal(50, exact);
        Assert.True(wasExact);
    }

    /// <summary>
    /// bm25 returns negative scores where more negative is better, so the ordering is ascending.
    /// Getting that backwards silently returns the worst matches first.
    /// </summary>
    [Fact]
    public void Better_matches_come_first()
    {
        var (db, search) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "harbour");
        Seed.Message(db, "tg/100/2",
            "a much longer message that mentions the harbour once among a great many other words "
            + "which dilutes how much this message is really about the harbour at all");

        var results = search.Search("harbour");

        Assert.Equal("tg/100/1", results[0].Uid);
    }

    [Fact]
    public void Searching_an_empty_archive_finds_nothing()
    {
        using var db = new TempDatabase();

        Assert.Empty(new ArchiveSearch(db.Database).Search("anything"));
    }
}
