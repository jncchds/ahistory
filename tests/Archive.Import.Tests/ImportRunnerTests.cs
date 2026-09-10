namespace Archive.Import.Tests;

public sealed class ImportRunnerTests
{
    [Fact]
    public void An_export_becomes_threads_identities_and_messages()
    {
        using var save = new TempSave();

        var stats = save.Import("group-and-dm", Exports.GroupAndDm());

        Assert.Equal(5, stats.MessagesSeen);
        Assert.Equal(5, stats.MessagesInserted);
        Assert.Equal(0, stats.MessagesSkipped);

        Assert.Equal(3, save.Scalar<long>("SELECT count(*) FROM thread;"));
        Assert.Equal(5, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    /// <summary>
    /// §1: accidentally merging a contact into the owner poisons the knowledge base, so the owner
    /// comes from the export's own personal_information rather than from a guess.
    /// </summary>
    [Fact]
    public void The_owner_is_seeded_from_the_export_and_linked_with_seed_confidence()
    {
        using var save = new TempSave();

        save.Import("group-and-dm", Exports.GroupAndDm());

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM person WHERE is_owner = 1;"));
        Assert.Equal("Owner Synthetic", save.Scalar<string>("SELECT display_name FROM person WHERE is_owner = 1;"));

        Assert.Equal("seed", save.Scalar<string>("""
            SELECT confidence FROM identity_person
            WHERE person_id = (SELECT id FROM person WHERE is_owner = 1);
            """));
    }

    [Fact]
    public void Every_identity_gets_a_person_so_the_archive_is_browsable_immediately()
    {
        using var save = new TempSave();

        save.Import("group-and-dm", Exports.GroupAndDm());

        var identities = save.Scalar<long>("SELECT count(*) FROM identity;");
        var links = save.Scalar<long>("SELECT count(*) FROM identity_person;");

        Assert.Equal(identities, links);
        Assert.True(identities >= 3);
    }

    /// <summary>
    /// The headline acceptance criterion (AGENTS.md P3).
    /// </summary>
    /// <remarks>
    /// The archive's content must be byte-identical after a second import of the same bytes. The
    /// import and message_import tables are excluded from the digest on purpose: a second import
    /// genuinely happened and is genuinely recorded, and pretending otherwise would be a weaker
    /// claim, not a stronger one.
    /// </remarks>
    [Fact]
    public void Re_importing_the_same_export_changes_nothing()
    {
        using var save = new TempSave();

        save.Import("group-and-dm", Exports.GroupAndDm());
        var before = save.Digest();
        var mediaBefore = save.MediaFiles().Length;

        var second = save.Import("group-and-dm", Exports.GroupAndDm());

        Assert.Equal(before, save.Digest());
        Assert.Equal(mediaBefore, save.MediaFiles().Length);

        Assert.Equal(0, second.MessagesInserted);
        Assert.Equal(second.MessagesSeen, second.MessagesSkipped);
        Assert.Equal(0, second.MessagesRevised);
    }

    /// <summary>
    /// The other half of the same story: the content did not change, but the archive knows the
    /// export was run twice. The run is recorded; the message's membership of the source is not
    /// re-recorded, because it did not change.
    /// </summary>
    [Fact]
    public void A_second_run_is_recorded_without_relinking_messages()
    {
        using var save = new TempSave();

        save.Import("group-and-dm", Exports.GroupAndDm());
        save.Import("group-and-dm", Exports.GroupAndDm());

        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM import;"));

        // One source, and one row per message in it — not one per message per run.
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM import_source;"));
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message_source WHERE message_id = 1;"));
        Assert.Equal(
            save.Scalar<long>("SELECT count(*) FROM message;"),
            save.Scalar<long>("SELECT count(*) FROM message_source;"));

        // Both runs belong to the same source.
        Assert.Equal(1, save.Scalar<long>("SELECT count(DISTINCT source_id) FROM import;"));
    }

    [Fact]
    public void Importing_a_superset_export_adds_only_the_new_messages()
    {
        using var save = new TempSave();

        var a = save.WriteExport("a", Export("""
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from": "Sam", "from_id": "user5001",
              "text": "first", "text_entities": [ { "type": "plain", "text": "first" } ] }
            """));

        var b = save.WriteExport("b", Export("""
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from": "Sam", "from_id": "user5001",
              "text": "first", "text_entities": [ { "type": "plain", "text": "first" } ] },
            { "id": 2, "type": "message", "date_unixtime": "1554221600", "from": "Sam", "from_id": "user5001",
              "text": "second", "text_entities": [ { "type": "plain", "text": "second" } ] }
            """));

        save.Import(a);
        var superset = save.Import(b);

        Assert.Equal(2, superset.MessagesSeen);
        Assert.Equal(1, superset.MessagesInserted);
        Assert.Equal(1, superset.MessagesSkipped);
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM message;"));

        // Re-importing the smaller export afterwards must not remove or duplicate anything.
        var again = save.Import(a);
        Assert.Equal(0, again.MessagesInserted);
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    /// <summary>
    /// §1: nothing imported is ever lost. The visible row carries the current text, and the
    /// superseded version becomes a revision rather than a second message or a silent overwrite.
    /// </summary>
    [Fact]
    public void An_edited_message_becomes_a_revision_not_a_duplicate()
    {
        using var save = new TempSave();

        var before = save.WriteExport("before", Export("""
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from": "Sam", "from_id": "user5001",
              "text": "train at 07:04", "text_entities": [ { "type": "plain", "text": "train at 07:04" } ] }
            """));

        var after = save.WriteExport("after", Export("""
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "edited_unixtime": "1554221999",
              "from": "Sam", "from_id": "user5001",
              "text": "train at 07:40", "text_entities": [ { "type": "plain", "text": "train at 07:40" } ] }
            """));

        save.Import(before);
        var edit = save.Import(after);

        Assert.Equal(1, edit.MessagesRevised);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message;"));

        Assert.Equal("train at 07:40", save.Scalar<string>("SELECT plaintext FROM message WHERE uid = 'tg/100/1';"));
        Assert.Equal("train at 07:04", save.Scalar<string>("SELECT plaintext FROM message_revision;"));
        Assert.NotNull(save.Scalar<string>("SELECT edited_at_utc FROM message WHERE uid = 'tg/100/1';"));
    }

    /// <summary>An edit must reach search, or the index quietly answers with the old text.</summary>
    [Fact]
    public void An_edit_is_reflected_in_search()
    {
        using var save = new TempSave();

        var before = save.WriteExport("before", Export("""
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from": "Sam", "from_id": "user5001",
              "text": "harbour", "text_entities": [ { "type": "plain", "text": "harbour" } ] }
            """));

        var after = save.WriteExport("after", Export("""
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from": "Sam", "from_id": "user5001",
              "text": "station", "text_entities": [ { "type": "plain", "text": "station" } ] }
            """));

        save.Import(before);
        save.Import(after);

        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM search_fts WHERE search_fts MATCH 'harbour';"));
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM search_fts WHERE search_fts MATCH 'station';"));
    }

    [Fact]
    public void Every_imported_message_is_searchable()
    {
        using var save = new TempSave();

        save.Import("group-and-dm", Exports.GroupAndDm());

        Assert.Equal(
            save.Scalar<long>("SELECT count(*) FROM message;"),
            save.Scalar<long>("SELECT count(*) FROM search_document WHERE provenance = 'message';"));

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM search_fts WHERE search_fts MATCH 'harbour';"));
    }

    /// <summary>§1: exports repeat the same sticker constantly; it must cost one file.</summary>
    [Fact]
    public void A_repeated_attachment_is_stored_once_and_referenced_twice()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.WriteForwards(save.ExportFolder("forwards")));

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM media WHERE media_kind = 'sticker';"));
        Assert.Equal(2, save.Scalar<long>("""
            SELECT count(*) FROM message_media mm
            JOIN media m ON m.hash = mm.media_hash
            WHERE m.media_kind = 'sticker';
            """));

        Assert.Equal(1, stats.MediaDeduplicated);
        Assert.Equal(3, save.MediaFiles().Length);
    }

    /// <summary>
    /// §2: a missing photo must not cost you the message it was attached to.
    /// </summary>
    [Fact]
    public void Media_the_export_omitted_does_not_fail_the_import()
    {
        using var save = new TempSave();

        var stats = save.Import("missing-media", Exports.MissingMedia());

        Assert.Equal(2, stats.MessagesInserted);
        Assert.Equal(2, stats.MediaMissing);
        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM media;"));

        Assert.Equal(2, save.Scalar<long>("""
            SELECT count(*) FROM message_media WHERE media_hash IS NULL AND missing_reason IS NOT NULL;
            """));
    }

    [Fact]
    public void A_split_export_is_one_import()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.WriteMultiFile(save.ExportFolder("multi-file")));

        Assert.Equal(2, stats.MessagesInserted);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM import;"));
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM thread;"));
    }

    [Fact]
    public void Service_messages_do_not_create_people_named_after_actions()
    {
        using var save = new TempSave();

        save.Import("service-messages", Exports.ServiceMessages());

        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM person WHERE display_name LIKE '%phone_call%';"));
        Assert.Equal(3, save.Scalar<long>("SELECT count(*) FROM message WHERE kind = 'service';"));
    }

    [Fact]
    public void An_export_folder_with_no_result_json_is_rejected_clearly()
    {
        using var save = new TempSave();

        var empty = save.WriteExport("empty", "{}");
        File.Delete(Path.Combine(empty, "result.json"));

        var exception = Assert.Throws<InvalidDataException>(() => save.Import(empty));

        // The message names what it looked for, which is what makes "wrong folder" fixable.
        Assert.Contains("does not look like an export", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Telegram", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A failed import is marked failed, not left looking like a short success.</summary>
    [Fact]
    public void A_failed_import_records_its_error()
    {
        using var save = new TempSave();

        Assert.ThrowsAny<Exception>(() => save.Import("unknown-prefix", Exports.UnknownPrefix()));

        Assert.Equal("failed", save.Scalar<string>("SELECT status FROM import;"));
        Assert.Contains("spaceship42", save.Scalar<string>("SELECT last_error FROM import;")!, StringComparison.Ordinal);
    }

    /// <summary>Wraps message JSON in the smallest export that will carry it.</summary>
    private static string Export(string messages) => $$"""
        {
          "name": "Sam Ruiz", "type": "personal_chat", "id": 100,
          "messages": [ {{messages}} ]
        }
        """;
}
