namespace Archive.Data.Tests;

/// <summary>
/// §4: a person's conversation is their direct threads in full, unioned with the messages they
/// sent in groups — stored once, assembled by query.
/// </summary>
public sealed class PersonConversationTests
{
    private static (TempDatabase Db, PersonConversation Conversation) Fixture()
    {
        var db = new TempDatabase();
        Seed.Basics(db);
        Seed.People(db);
        Seed.Participants(db);

        return (db, new PersonConversation(db.Database));
    }

    /// <summary>A conversation is both halves of it, not only what the other person said.</summary>
    [Fact]
    public void A_direct_thread_contributes_both_sides()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/100/1", "theirs", Seed.IdentityId, 1000);
        Seed.MessageFrom(db, "tg/100/2", "mine", Seed.OwnerIdentityId, 1001);

        var page = conversation.Page(Seed.SamPersonId);

        Assert.Equal(2, page.Messages.Count);
        Assert.All(page.Messages, m => Assert.Equal("dm", m.Origin));
        Assert.Contains(page.Messages, m => m.Plaintext == "mine");
    }

    /// <summary>
    /// In a group, only what they said belongs to their conversation. Everything else is context,
    /// fetched when asked for rather than folded in.
    /// </summary>
    [Fact]
    public void A_group_contributes_only_what_that_person_said()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/200/1", "sam in the group", Seed.IdentityId, 2000, Seed.OtherThreadId);
        Seed.MessageFrom(db, "tg/200/2", "me in the group", Seed.OwnerIdentityId, 2001, Seed.OtherThreadId);

        var page = conversation.Page(Seed.SamPersonId);

        var message = Assert.Single(page.Messages);

        Assert.Equal("sam in the group", message.Plaintext);
        Assert.Equal("group", message.Origin);
        Assert.True(message.IsFromGroup);
        Assert.Equal("Trip", message.ThreadTitle);
    }

    [Fact]
    public void Direct_and_group_messages_arrive_in_one_time_ordered_stream()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/100/1", "dm first", Seed.IdentityId, 1000);
        Seed.MessageFrom(db, "tg/200/1", "group second", Seed.IdentityId, 2000, Seed.OtherThreadId);
        Seed.MessageFrom(db, "tg/100/2", "dm third", Seed.IdentityId, 3000);

        var page = conversation.Page(Seed.SamPersonId);

        Assert.Equal(
            ["dm third", "group second", "dm first"],
            page.Messages.Select(m => m.Plaintext));
    }

    /// <summary>
    /// The keyset tie-break again, this time across a union: without the id, a page boundary
    /// landing inside a group of same-second messages drops or repeats them.
    /// </summary>
    [Fact]
    public void Paging_never_repeats_or_skips_across_a_timestamp_tie()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        for (var i = 1; i <= 30; i++)
        {
            Seed.MessageFrom(db, $"tg/100/{i}", $"dm {i}", Seed.IdentityId, 5000);
        }

        for (var i = 1; i <= 30; i++)
        {
            Seed.MessageFrom(db, $"tg/200/{i}", $"group {i}", Seed.IdentityId, 5000, Seed.OtherThreadId);
        }

        var seen = new List<string>();
        var page = conversation.Page(Seed.SamPersonId, limit: 7);

        while (true)
        {
            seen.AddRange(page.Messages.Select(m => m.Uid));

            if (!page.HasMore)
            {
                break;
            }

            page = conversation.Page(Seed.SamPersonId, 7, page.NextBeforeUnix, page.NextBeforeId);
        }

        Assert.Equal(60, seen.Count);
        Assert.Equal(60, seen.Distinct().Count());
    }

    /// <summary>
    /// Every arm of the union must be an index range scan.
    /// </summary>
    /// <remarks>
    /// The merged rows are then sorted once, which SQLite reports as a temp b-tree. That is
    /// expected and bounded: the sort is over at most (arms × limit) rows, not over the archive.
    /// The failure this guards against is a full scan of `message`, which a fixture would never
    /// reveal by timing alone.
    /// </remarks>
    [Fact]
    public void Every_arm_of_the_union_uses_an_index()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/100/1", "one", Seed.IdentityId, 1000);

        var plan = string.Join(" | ", conversation.ExplainPage(Seed.SamPersonId));

        Assert.DoesNotContain("SCAN message", plan, StringComparison.Ordinal);
        Assert.Contains("ix_message_thread_time", plan, StringComparison.Ordinal);
        Assert.Contains("ix_message_sender_time", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void A_person_with_no_accounts_has_an_empty_conversation()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        db.Execute("""
            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p:nobody', 'Nobody', 0, '2020-01-01T00:00:00.0000000+00:00');
            """);

        var page = conversation.Page("p:nobody");

        Assert.Empty(page.Messages);
        Assert.False(page.HasMore);
    }

    /// <summary>
    /// §4: "yeah exactly" is nonsense alone. Expanding a group line shows what it was answering.
    /// </summary>
    [Fact]
    public void Context_returns_the_messages_around_a_group_line()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        for (var i = 1; i <= 20; i++)
        {
            var sender = i == 10 ? Seed.IdentityId : Seed.OwnerIdentityId;
            Seed.MessageFrom(db, $"tg/200/{i}", $"line {i}", sender, 1000 + i, Seed.OtherThreadId);
        }

        var page = conversation.Page(Seed.SamPersonId);
        var groupLine = Assert.Single(page.Messages, m => m.IsFromGroup);

        var context = conversation.Context(groupLine.Id, radius: 3);

        // The anchor plus three either side, oldest first so it reads as a conversation.
        Assert.Equal(7, context.Count);
        Assert.Equal("line 7", context[0].Plaintext);
        Assert.Equal("line 10", context[3].Plaintext);
        Assert.Equal("line 13", context[^1].Plaintext);
    }

    [Fact]
    public void Context_at_the_start_of_a_thread_does_not_run_off_the_end()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/200/1", "first ever", Seed.IdentityId, 1000, Seed.OtherThreadId);
        Seed.MessageFrom(db, "tg/200/2", "second", Seed.OwnerIdentityId, 1001, Seed.OtherThreadId);

        var page = conversation.Page(Seed.SamPersonId);
        var context = conversation.Context(page.Messages[0].Id, radius: 5);

        Assert.Equal(2, context.Count);
        Assert.Equal("first ever", context[0].Plaintext);
    }

    /// <summary>
    /// A merge is repointing identities, so a person's conversation must follow their accounts
    /// rather than being fixed at import time (§1).
    /// </summary>
    [Fact]
    public void Merging_a_second_account_brings_its_messages_into_the_conversation()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        db.Execute($"""
            INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
            VALUES ('idn-2', 'telegram', '5099', 'Sam (work)', '{Seed.ImportId}', '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p:idn-2', 'Sam (work)', 0, '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
            VALUES ('idn-2', 'p:idn-2', 'auto', '2020-01-01T00:00:00.0000000+00:00');
            """);

        Seed.MessageFrom(db, "tg/200/1", "from the other account", "idn-2", 2000, Seed.OtherThreadId);
        Seed.MessageFrom(db, "tg/100/1", "from the first", Seed.IdentityId, 1000);

        Assert.Single(conversation.Page(Seed.SamPersonId).Messages);

        new IdentityMerger(db.Database).MergeInto("idn-2", Seed.SamPersonId);

        Assert.Equal(2, conversation.Page(Seed.SamPersonId).Messages.Count);
    }
}
