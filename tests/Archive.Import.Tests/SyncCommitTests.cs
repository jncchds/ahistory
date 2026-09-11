using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Import.Tests;

/// <summary>
/// What the committer does for a source that is read from a platform rather than from a folder:
/// deletions, cursors, messages that reach the archive by two routes, and short transactions.
/// </summary>
public sealed class SyncCommitTests
{
    private const string OneMessage = """
        { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [
          { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
            "from": "Sam", "text": "train at 07:40",
            "text_entities": [ { "type": "plain", "text": "train at 07:40" } ] } ] }
        """;

    private static ImportCommitter Connector(TempSave save) =>
        new(save.Database, "telegram", "telegram:account:1", "Me (Telegram)", "telegram:api", "cursor");

    [Fact]
    public void A_message_the_platform_deleted_is_kept_and_marked()
    {
        using var save = new TempSave();
        save.Import(save.WriteExport("chat", OneMessage));

        using (var committer = Connector(save))
        {
            Assert.True(committer.MarkDeleted("tg/100/1"));
            committer.Complete(optimize: false);

            Assert.Equal(1, committer.Stats.MessagesDeleted);
        }

        // P2: nothing about the message itself changed.
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message;"));
        Assert.Equal("train at 07:40", save.Scalar<string>("SELECT plaintext FROM message;"));

        var row = Assert.Single(new ArchiveQueries(save.Database).ThreadMessages("telegram:100").Messages);

        Assert.True(row.IsDeletedOnPlatform);
    }

    [Fact]
    public void The_person_view_knows_a_message_was_deleted_too()
    {
        using var save = new TempSave();
        save.Import(save.WriteExport("chat", OneMessage));

        using (var committer = Connector(save))
        {
            committer.MarkDeleted("tg/100/1");
            committer.Complete(optimize: false);
        }

        var id = save.Scalar<long>("SELECT id FROM message;");
        var row = Assert.Single(new PersonConversation(save.Database).Context(id));

        Assert.True(row.IsDeletedOnPlatform);
        Assert.NotNull(row.DeletedObservedUtc);
    }

    /// <summary>
    /// A deletion is news once. Telegram repeats updates after a reconnect, and a message
    /// the archive never had — a chat nobody included — is nothing to record.
    /// </summary>
    [Fact]
    public void A_deletion_is_recorded_once_and_only_for_a_message_the_archive_has()
    {
        using var save = new TempSave();
        save.Import(save.WriteExport("chat", OneMessage));

        using var committer = Connector(save);

        Assert.True(committer.MarkDeleted("tg/100/1"));
        Assert.False(committer.MarkDeleted("tg/100/1"));
        Assert.False(committer.MarkDeleted("tg/100/999"));

        committer.Complete(optimize: false);

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message_deletion;"));
    }

    /// <summary>
    /// The same message by two routes names its photo two ways. That is not an edit.
    /// </summary>
    [Fact]
    public void A_message_whose_only_difference_is_where_its_attachment_came_from_is_not_an_edit()
    {
        using var save = new TempSave();

        save.Import(save.WriteExport("export", """
            { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
                "photo": "photos/photo_1@02-04-2019_17-12-03.jpg",
                "text": "look", "text_entities": [ { "type": "plain", "text": "look" } ] } ] }
            """));

        var second = save.Import(save.WriteExport("api", """
            { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
                "photo": "telegram:photo/5566",
                "text": "look", "text_entities": [ { "type": "plain", "text": "look" } ] } ] }
            """));

        Assert.Equal(0, second.MessagesRevised);
        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM message_revision;"));
    }

    /// <summary>
    /// An export keeps Telegram's indentation inside <c>text_entities</c>; a connector writes the
    /// same array compactly. Same words, same entities, not an edit.
    /// </summary>
    [Fact]
    public void Entities_that_differ_only_in_whitespace_are_not_an_edit()
    {
        using var save = new TempSave();

        save.Import(save.WriteExport("indented", """
            { "name": "Sam", "type": "personal_chat", "id": 100, "messages": [
              { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
                "text": "see here",
                "text_entities": [
                  { "type": "plain", "text": "see " },
                  { "type": "text_link", "text": "here", "href": "https://example.org/" }
                ] } ] }
            """));

        var second = save.Import(save.WriteExport("compact", """
            {"name":"Sam","type":"personal_chat","id":100,"messages":[{"id":1,"type":"message","date_unixtime":"1554221523","from_id":"user5001","text":"see here","text_entities":[{"type":"plain","text":"see "},{"type":"text_link","text":"here","href":"https://example.org/"}]}]}
            """));

        Assert.Equal(0, second.MessagesRevised);
    }

    /// <summary>A changed link target is a change to what was said, even with the same words.</summary>
    [Fact]
    public void A_changed_entity_is_still_an_edit()
    {
        using var save = new TempSave();

        save.Import(save.WriteExport("before", """
            {"name":"Sam","type":"personal_chat","id":100,"messages":[{"id":1,"type":"message","date_unixtime":"1554221523","from_id":"user5001","text":"here","text_entities":[{"type":"text_link","text":"here","href":"https://example.org/a"}]}]}
            """));

        var second = save.Import(save.WriteExport("after", """
            {"name":"Sam","type":"personal_chat","id":100,"messages":[{"id":1,"type":"message","date_unixtime":"1554221523","from_id":"user5001","text":"here","text_entities":[{"type":"text_link","text":"here","href":"https://example.org/b"}]}]}
            """));

        Assert.Equal(1, second.MessagesRevised);
    }

    [Fact]
    public void A_cursor_is_stored_per_scope_and_replaced_rather_than_repeated()
    {
        using var save = new TempSave();
        using var committer = Connector(save);

        committer.SetSyncState("100", "{\"newest\":5}");
        committer.SetSyncState("100", "{\"newest\":9}");
        committer.SetSyncState("*", "{}");
        committer.Complete(optimize: false);

        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM sync_state;"));
        Assert.Equal("{\"newest\":9}", save.Scalar<string>("SELECT cursor FROM sync_state WHERE scope = '100';"));
    }

    /// <summary>
    /// A committer leaves no write transaction open between batches.
    /// </summary>
    /// <remarks>
    /// A connector spends minutes between pages waiting on the network. A committer that opened
    /// its next transaction the moment it committed the last one held SQLite's single write lock
    /// for all of that time, and every other writer in the app — the AI runner, a folder import —
    /// waited behind it. With the busy timeout at zero, a lock held by the committer fails this
    /// write immediately instead of hiding behind a five-second wait.
    /// </remarks>
    [Fact]
    public void No_write_lock_is_held_between_batches()
    {
        using var save = new TempSave();
        using var committer = Connector(save);

        committer.SetSyncState("100", "a");
        committer.Checkpoint();

        using var other = new SqliteConnection(save.Database.ConnectionString);
        other.Open();

        using var command = other.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 0;
            BEGIN IMMEDIATE;
            UPDATE sync_state SET cursor = 'b';
            COMMIT;
            """;

        command.ExecuteNonQuery();

        Assert.Equal("b", save.Scalar<string>("SELECT cursor FROM sync_state;"));

        SqliteConnection.ClearPool(other);
    }
}
