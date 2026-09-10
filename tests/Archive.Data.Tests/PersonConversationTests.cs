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

    /// <summary>Twenty messages in the direct thread, a minute apart, oldest first.</summary>
    private static PersonMessageRow Numbered(
        TempDatabase db, PersonConversation conversation, string plaintext)
    {
        for (var i = 1; i <= 20; i++)
        {
            Seed.MessageFrom(db, $"tg/100/{i}", $"message {i}", Seed.IdentityId, 1000 + i);
        }

        return conversation.Page(Seed.SamPersonId, 100).Messages.Single(m => m.Plaintext == plaintext);
    }

    /// <summary>
    /// Revealing a search hit anchors a page on the hit, so the hit has to be in it.
    /// </summary>
    /// <remarks>
    /// An exclusive cursor loads everything up to the message that was asked for and stops one
    /// row short of it, which looks exactly like the archive having lost it.
    /// </remarks>
    [Fact]
    public void An_inclusive_page_ends_on_the_message_it_is_anchored_to()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        var tenth = Numbered(db, conversation, "message 10");

        var page = conversation.Page(Seed.SamPersonId, 5, tenth.SentAtUnix, tenth.Id, inclusive: true);

        Assert.Equal("message 10", page.Messages[0].Plaintext);
        Assert.Equal("message 6", page.Messages[^1].Plaintext);
    }

    [Fact]
    public void An_exclusive_page_starts_below_the_message_it_is_anchored_to()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        var tenth = Numbered(db, conversation, "message 10");

        var page = conversation.Page(Seed.SamPersonId, 5, tenth.SentAtUnix, tenth.Id);

        Assert.Equal("message 9", page.Messages[0].Plaintext);
        Assert.DoesNotContain(page.Messages, m => m.Plaintext == "message 10");
    }

    /// <summary>
    /// The other half of landing in the middle: what came after, in reading order.
    /// </summary>
    [Fact]
    public void Reading_forwards_returns_what_came_next_oldest_first()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        var tenth = Numbered(db, conversation, "message 10");

        var page = conversation.PageAfter(Seed.SamPersonId, tenth.SentAtUnix, tenth.Id, 5);

        Assert.Equal(
            ["message 11", "message 12", "message 13", "message 14", "message 15"],
            page.Messages.Select(m => m.Plaintext));

        Assert.True(page.HasMore);
    }

    [Fact]
    public void Reading_forwards_reaches_the_end_without_repeating_anything()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        var first = Numbered(db, conversation, "message 1");

        var seen = new List<string>();
        var page = conversation.PageAfter(Seed.SamPersonId, first.SentAtUnix, first.Id, 7);

        while (true)
        {
            seen.AddRange(page.Messages.Select(m => m.Plaintext));

            if (!page.HasMore)
            {
                break;
            }

            page = conversation.PageAfter(
                Seed.SamPersonId, page.NextAfterUnix!.Value, page.NextAfterId!.Value, 7);
        }

        // Nineteen: everything after the first message, each of them once.
        Assert.Equal(19, seen.Count);
        Assert.Equal(19, seen.Distinct().Count());
        Assert.Equal("message 20", seen[^1]);
    }

    /// <summary>
    /// A direct message belongs to the person at the other end of it, whichever way it went.
    /// </summary>
    /// <remarks>
    /// Attributing your own line to yourself would open it in a conversation with yourself, which
    /// is the same phantom self-chat D26 was about.
    /// </remarks>
    [Fact]
    public void Your_own_direct_message_belongs_to_the_person_you_sent_it_to()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/100/1", "mine", Seed.OwnerIdentityId, 1000);

        var message = Assert.Single(conversation.Page(Seed.SamPersonId).Messages);

        Assert.Equal(Seed.SamPersonId, conversation.PersonOf(message.Id));
    }

    /// <summary>A group line belongs to whoever said it — there is no other end to a room.</summary>
    [Fact]
    public void A_group_line_belongs_to_whoever_said_it()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Seed.MessageFrom(db, "tg/200/1", "sam in the group", Seed.IdentityId, 2000, Seed.OtherThreadId);
        Seed.MessageFrom(db, "tg/200/2", "me in the group", Seed.OwnerIdentityId, 2001, Seed.OtherThreadId);

        var sam = Assert.Single(conversation.Page(Seed.SamPersonId).Messages);
        var mine = Assert.Single(conversation.Page(Seed.OwnerPersonId).Messages);

        Assert.Equal(Seed.SamPersonId, conversation.PersonOf(sam.Id));
        Assert.Equal(Seed.OwnerPersonId, conversation.PersonOf(mine.Id));
    }

    [Fact]
    public void A_message_that_is_not_there_locates_to_nothing()
    {
        var (db, conversation) = Fixture();
        using var _ = db;

        Assert.Null(conversation.Locate(9_999));
        Assert.Null(conversation.PersonOf(9_999));
    }
}
