namespace Archive.Data.Tests;

public sealed class ArchiveQueriesTests
{
    private static (TempDatabase Db, ArchiveQueries Queries) Fixture()
    {
        var db = new TempDatabase();
        Seed.Basics(db);
        Seed.People(db);

        return (db, new ArchiveQueries(db.Database));
    }

    [Fact]
    public void The_summary_counts_what_the_overview_shows()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "one");
        Seed.Message(db, "tg/100/2", "two");

        var summary = queries.Summary();

        Assert.Equal(2, summary.Messages);
        Assert.Equal(2, summary.Threads);
        Assert.Equal("Kirill", summary.OwnerName);
        Assert.Equal(1, summary.Sources);
    }

    [Fact]
    public void An_empty_save_summarises_without_falling_over()
    {
        using var db = new TempDatabase();

        var summary = new ArchiveQueries(db.Database).Summary();

        Assert.Equal(0, summary.Messages);
        Assert.Null(summary.OwnerName);
        Assert.Null(summary.LastImportUtc);
    }

    [Fact]
    public void People_are_listed_owner_first()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "one");

        var people = queries.People();

        Assert.Equal(2, people.Count);
        Assert.True(people[0].IsOwner);
        Assert.Equal("Kirill", people[0].DisplayName);
    }

    [Fact]
    public void A_persons_message_count_follows_their_identities()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "one");
        Seed.Message(db, "tg/100/2", "two");

        var sam = Assert.Single(queries.People(), p => !p.IsOwner);

        Assert.Equal(2, sam.MessageCount);
        Assert.Equal(1, sam.IdentityCount);
    }

    [Fact]
    public void Identities_report_who_they_currently_point_at()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        var identities = queries.Identities();

        Assert.Equal(2, identities.Count);
        Assert.Contains(identities, i => i.PersonName == "Kirill" && i.Confidence == "seed");
        Assert.Contains(identities, i => i.PersonName == "Sam" && i.Confidence == "auto");
    }

    [Fact]
    public void Threads_report_their_size_and_most_recent_message()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "one");
        Seed.Message(db, "tg/200/1", "group", Seed.OtherThreadId);

        var threads = queries.Threads();

        Assert.Equal(2, threads.Count);
        Assert.All(threads, t => Assert.Equal(1, t.MessageCount));
        Assert.All(threads, t => Assert.NotNull(t.LastMessageUnix));
    }

    /// <summary>
    /// The paging invariant that matters: at second-granularity timestamps — which Telegram
    /// produces constantly — a cursor without an id tie-break drops or repeats messages at every
    /// page boundary.
    /// </summary>
    [Fact]
    public void Keyset_paging_never_repeats_or_skips_across_a_timestamp_tie()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        // Twenty messages all claiming the same second.
        for (var i = 1; i <= 20; i++)
        {
            Seed.Message(db, $"tg/100/{i}", $"message {i}");
        }

        var seen = new List<string>();
        var page = queries.ThreadMessages(Seed.ThreadId, limit: 7);

        while (true)
        {
            seen.AddRange(page.Messages.Select(m => m.Uid));

            if (!page.HasMore)
            {
                break;
            }

            page = queries.ThreadMessages(Seed.ThreadId, 7, page.NextBeforeUnix, page.NextBeforeId);
        }

        Assert.Equal(20, seen.Count);
        Assert.Equal(20, seen.Distinct().Count());
    }

    [Fact]
    public void Messages_come_back_newest_first()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "older");
        db.Execute("INSERT INTO message (uid, thread_id, sender_identity_id, kind, sent_at_utc, sent_at_unix, plaintext, content_hash, first_import_id, importer_version) "
                 + $"VALUES ('tg/100/2', '{Seed.ThreadId}', '{Seed.IdentityId}', 'message', '2021-01-01T12:00:00.0000000+00:00', 1609502400, 'newer', 'h2', '{Seed.ImportId}', 'test');");

        var page = queries.ThreadMessages(Seed.ThreadId);

        Assert.Equal("newer", page.Messages[0].Plaintext);
        Assert.Equal("older", page.Messages[1].Plaintext);
    }

    [Fact]
    public void A_message_from_the_owner_is_marked_as_such()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        db.Execute("INSERT INTO message (uid, thread_id, sender_identity_id, kind, sent_at_utc, sent_at_unix, plaintext, content_hash, first_import_id, importer_version) "
                 + $"VALUES ('tg/100/9', '{Seed.ThreadId}', '{Seed.OwnerIdentityId}', 'message', '2021-01-01T12:00:00.0000000+00:00', 1609502400, 'mine', 'h9', '{Seed.ImportId}', 'test');");
        Seed.Message(db, "tg/100/1", "theirs");

        var page = queries.ThreadMessages(Seed.ThreadId);

        Assert.True(Assert.Single(page.Messages, m => m.Plaintext == "mine").FromOwner);
        Assert.False(Assert.Single(page.Messages, m => m.Plaintext == "theirs").FromOwner);
    }

    /// <summary>
    /// The paging query must be an index scan. A sequential scan is invisible on a fixture and
    /// ruinous at half a million rows, so the plan itself is asserted.
    /// </summary>
    [Fact]
    public void Thread_paging_uses_an_index_rather_than_scanning()
    {
        var (db, queries) = Fixture();
        using var _ = db;

        Seed.Message(db, "tg/100/1", "one");
        queries.ThreadMessages(Seed.ThreadId);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT m.id FROM message m
            WHERE m.thread_id = 'thr-1'
              AND (m.sent_at_unix < 99999999 OR (m.sent_at_unix = 99999999 AND m.id < 5))
            ORDER BY m.sent_at_unix DESC, m.id DESC
            LIMIT 10;
            """;

        var plan = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            plan.Add(reader.GetString(reader.FieldCount - 1));
        }

        var text = string.Join(" | ", plan);

        Assert.Contains("ix_message_thread_time", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN message", text, StringComparison.Ordinal);
    }
}
