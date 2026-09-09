namespace Archive.Data.Tests;

/// <summary>
/// The FTS5 index is maintained entirely by triggers, and an external-content FTS5 table does
/// not notice changes to its content table on its own. These tests exist because every failure
/// mode here is silent: the index keeps answering queries, just with the wrong answers.
/// </summary>
public sealed class SearchIndexTests
{
    private static long Matches(TempDatabase db, string term) =>
        db.Scalar<long>($"SELECT count(*) FROM search_fts WHERE search_fts MATCH '{term}';");

    [Fact]
    public void Inserting_a_message_indexes_it()
    {
        using var db = new TempDatabase();
        Seed.Basics(db);

        Seed.Message(db, "tg/100/1", "the harbour was freezing");

        Assert.Equal(1, Matches(db, "harbour"));
        Assert.Equal(1, db.Scalar<long>("SELECT count(*) FROM search_document WHERE provenance = 'message';"));
    }

    [Fact]
    public void Editing_a_message_reindexes_it()
    {
        using var db = new TempDatabase();
        Seed.Basics(db);
        Seed.Message(db, "tg/100/1", "the harbour was freezing");

        db.Execute("UPDATE message SET plaintext = 'the station was freezing' WHERE uid = 'tg/100/1';");

        Assert.Equal(0, Matches(db, "harbour"));
        Assert.Equal(1, Matches(db, "station"));
    }

    [Fact]
    public void Deleting_a_message_removes_it_from_the_index()
    {
        using var db = new TempDatabase();
        Seed.Basics(db);
        Seed.Message(db, "tg/100/1", "the harbour was freezing");

        db.Execute("DELETE FROM message WHERE uid = 'tg/100/1';");

        Assert.Equal(0, Matches(db, "harbour"));
        Assert.Equal(0, db.Scalar<long>("SELECT count(*) FROM search_document;"));
    }

    /// <summary>
    /// The cascade case, and the reason Database.Configure sets PRAGMA recursive_triggers.
    /// Deleting a thread cascades to its messages and on to search_document; without recursive
    /// triggers those cascaded deletes fire no trigger, and search_fts keeps postings for
    /// messages that no longer exist.
    /// </summary>
    [Fact]
    public void Deleting_a_thread_removes_its_messages_from_the_index()
    {
        using var db = new TempDatabase();
        Seed.Basics(db);
        Seed.Message(db, "tg/100/1", "the harbour was freezing");
        Seed.Message(db, "tg/200/1", "unrelated group chatter", Seed.OtherThreadId);

        db.Execute($"DELETE FROM thread WHERE id = '{Seed.ThreadId}';");

        Assert.Equal(0, Matches(db, "harbour"));
        Assert.Equal(1, Matches(db, "chatter"));
        Assert.Equal(1, db.Scalar<long>("SELECT count(*) FROM search_document;"));
    }

    /// <summary>
    /// The archive is multilingual. unicode61 must case-fold Cyrillic, or half the archive is
    /// only findable by typing the exact case it was written in.
    /// </summary>
    [Fact]
    public void Cyrillic_terms_match_case_insensitively()
    {
        using var db = new TempDatabase();
        Seed.Basics(db);
        Seed.Message(db, "tg/100/1", "Прага была холодной");

        Assert.Equal(1, Matches(db, "прага"));
        Assert.Equal(1, Matches(db, "ПРАГА"));
    }

    /// <summary>
    /// There is no FTS5 stemmer for Slavic languages, so inflected forms are reached with a
    /// prefix query instead (decisions.md D8). This is the behaviour FtsQueryBuilder will rely
    /// on in M6.
    /// </summary>
    [Fact]
    public void Prefix_expansion_reaches_an_inflected_form()
    {
        using var db = new TempDatabase();
        Seed.Basics(db);
        Seed.Message(db, "tg/100/1", "мы были в Праге весной");

        Assert.Equal(0, Matches(db, "Прага"));
        Assert.Equal(1, Matches(db, "Праг*"));
    }
}
