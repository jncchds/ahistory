namespace Archive.Import.Tests;

/// <summary>
/// A source is where data came from; a run is one act of importing it. Re-exporting an account
/// six months later produces a newer version of one source, not a second source — which is what
/// keeps a re-import proportional to what changed rather than to the size of the export.
/// </summary>
public sealed class ImportSourceTests
{
    [Fact]
    public void An_export_that_names_its_account_becomes_a_source_named_after_it()
    {
        using var save = new TempSave();

        save.ImportFixture("group-and-dm");

        Assert.Equal(
            "telegram:account:777001",
            save.Scalar<string>("SELECT id FROM import_source;"));

        Assert.Equal("Kirill Chekanov (Telegram)", save.Scalar<string>("SELECT label FROM import_source;"));
    }

    /// <summary>
    /// The case this whole model exists for: a later export of the same account carries the
    /// messages you already have plus the ones since. Only the new ones gain a source row.
    /// </summary>
    [Fact]
    public void A_newer_export_of_the_same_account_extends_the_existing_source()
    {
        using var save = new TempSave();

        var january = save.WriteExport("january", Export(777001, """
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
              "text": "first", "text_entities": [ { "type": "plain", "text": "first" } ] }
            """));

        var june = save.WriteExport("june", Export(777001, """
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
              "text": "first", "text_entities": [ { "type": "plain", "text": "first" } ] },
            { "id": 2, "type": "message", "date_unixtime": "1560000000", "from_id": "user5001",
              "text": "months later", "text_entities": [ { "type": "plain", "text": "months later" } ] }
            """));

        save.Import(january);
        var sourceRowsAfterFirst = save.Scalar<long>("SELECT count(*) FROM message_source;");

        save.Import(june);

        Assert.Equal(1, sourceRowsAfterFirst);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM import_source;"));
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM import;"));

        // Two messages, two source rows — the pre-existing one was not written again.
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM message_source;"));
    }

    /// <summary>
    /// The run-level answer stays available: what a given run added is what withdrawing it would
    /// remove.
    /// </summary>
    [Fact]
    public void What_a_particular_run_added_is_queryable()
    {
        using var save = new TempSave();

        var first = save.WriteExport("first", Export(777001, """
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
              "text": "a", "text_entities": [ { "type": "plain", "text": "a" } ] }
            """));

        var second = save.WriteExport("second", Export(777001, """
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
              "text": "a", "text_entities": [ { "type": "plain", "text": "a" } ] },
            { "id": 2, "type": "message", "date_unixtime": "1560000000", "from_id": "user5001",
              "text": "b", "text_entities": [ { "type": "plain", "text": "b" } ] }
            """));

        save.Import(first);
        save.Import(second);

        var runs = save.ImportIdsInOrder();

        Assert.Equal(1, save.Scalar<long>($"SELECT count(*) FROM message_source WHERE first_import_id = '{runs[0]}';"));
        Assert.Equal(1, save.Scalar<long>($"SELECT count(*) FROM message_source WHERE first_import_id = '{runs[1]}';"));
    }

    [Fact]
    public void The_preview_suggests_the_matching_account_and_says_why()
    {
        using var save = new TempSave();

        save.ImportFixture("group-and-dm");

        var preview = save.Runner.Preview(Fixtures.Directory("group-and-dm"));

        Assert.Equal("777001", preview.DetectedAccountId);
        Assert.Equal("Kirill Chekanov", preview.DetectedAccountName);
        Assert.Equal("telegram:account:777001", preview.SuggestedSourceId);
        Assert.True(preview.SuggestedSourceExists);
        Assert.Contains("same account", preview.SuggestionReason, StringComparison.OrdinalIgnoreCase);

        var option = Assert.Single(preview.ExistingSources);
        Assert.True(option.IsSuggested);
        Assert.Equal(5, option.MessageCount);
    }

    [Fact]
    public void The_preview_writes_nothing()
    {
        using var save = new TempSave();

        save.Runner.Preview(Fixtures.Directory("group-and-dm"));

        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM import;"));
        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM import_source;"));
        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    /// <summary>
    /// A single-chat export names no account. With one Telegram source already present it is
    /// almost certainly part of it — almost, which is why this is a suggestion and not a decision.
    /// </summary>
    [Fact]
    public void An_export_that_names_no_account_is_suggested_to_join_the_only_existing_source()
    {
        using var save = new TempSave();

        save.ImportFixture("group-and-dm");

        var preview = save.Runner.Preview(Fixtures.Directory("single-chat"));

        Assert.Null(preview.DetectedAccountId);
        Assert.Equal("telegram:account:777001", preview.SuggestedSourceId);
        Assert.True(preview.SuggestedSourceExists);
        Assert.Contains("does not name its account", preview.SuggestionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_first_export_that_names_no_account_starts_a_source_named_after_the_folder()
    {
        using var save = new TempSave();

        var preview = save.Runner.Preview(Fixtures.Directory("single-chat"));

        Assert.Equal("telegram:folder:single-chat", preview.SuggestedSourceId);
        Assert.False(preview.SuggestedSourceExists);
    }

    /// <summary>
    /// The user's answer wins over the suggestion. This is the path the import UI drives: an
    /// archive someone handed you can overlap your own entirely and still be a separate source.
    /// </summary>
    [Fact]
    public void The_caller_can_override_the_suggested_source()
    {
        using var save = new TempSave();

        save.ImportFixture("group-and-dm");
        save.Runner.Run(Fixtures.Directory("group-and-dm"), sourceId: "telegram:account:999");

        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM import_source;"));

        // The same messages now belong to both sources, which is exactly what the filter is for.
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM message_source WHERE message_id = 1;"));
        Assert.Equal(5, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    [Fact]
    public void Filtering_by_source_selects_that_sources_messages()
    {
        using var save = new TempSave();

        save.ImportFixture("group-and-dm");
        save.Runner.Run(Fixtures.Directory("single-chat"), sourceId: "telegram:account:999");

        Assert.Equal(5, save.Scalar<long>("""
            SELECT count(*) FROM message m
            JOIN message_source ms ON ms.message_id = m.id
            WHERE ms.source_id = 'telegram:account:777001';
            """));

        Assert.Equal(1, save.Scalar<long>("""
            SELECT count(*) FROM message m
            JOIN message_source ms ON ms.message_id = m.id
            WHERE ms.source_id = 'telegram:account:999';
            """));
    }

    /// <summary>Re-running must not overwrite a name the user chose.</summary>
    [Fact]
    public void A_re_run_does_not_rename_the_source()
    {
        using var save = new TempSave();

        save.ImportFixture("group-and-dm");
        save.Execute("UPDATE import_source SET label = 'My Telegram';");

        save.ImportFixture("group-and-dm");

        Assert.Equal("My Telegram", save.Scalar<string>("SELECT label FROM import_source;"));
    }

    /// <summary>
    /// A save is one person's archive (decisions.md D13). A second account of theirs attaches to
    /// the same owner rather than creating a rival one — which is right for a personal and a work
    /// Telegram, and is exactly what would be wrong for someone else's archive.
    /// </summary>
    [Fact]
    public void A_second_account_attaches_to_the_existing_owner()
    {
        using var save = new TempSave();

        save.Import(save.WriteExport("personal", Export(777001, """
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
              "text": "a", "text_entities": [ { "type": "plain", "text": "a" } ] }
            """)));

        save.Import(save.WriteExport("work", Export(888002, """
            { "id": 9, "type": "message", "date_unixtime": "1554300000", "from_id": "user5003",
              "text": "b", "text_entities": [ { "type": "plain", "text": "b" } ] }
            """)));

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM person WHERE is_owner = 1;"));

        // Both accounts point at the one owner.
        Assert.Equal(2, save.Scalar<long>("""
            SELECT count(*) FROM identity_person
            WHERE person_id = (SELECT id FROM person WHERE is_owner = 1);
            """));
    }

    /// <summary>
    /// The importer cannot tell a second account of yours from an archive someone handed you, so
    /// it does not guess — it reports that the account is new and lets the user act.
    /// </summary>
    [Fact]
    public void An_account_that_is_not_yet_the_owners_is_flagged()
    {
        using var save = new TempSave();

        save.ImportFixture("group-and-dm");

        var mine = save.Runner.Preview(Fixtures.Directory("group-and-dm"));
        Assert.False(mine.AccountIsNewToOwner);
        Assert.Equal("Kirill Chekanov", mine.OwnerName);

        var stranger = save.WriteExport("stranger", Export(999003, """
            { "id": 1, "type": "message", "date_unixtime": "1554221523", "from_id": "user5001",
              "text": "x", "text_entities": [ { "type": "plain", "text": "x" } ] }
            """));

        var preview = save.Runner.Preview(stranger);

        Assert.True(preview.AccountIsNewToOwner);
        Assert.Equal("999003", preview.DetectedAccountId);
        Assert.Equal("Kirill Chekanov", preview.OwnerName);
    }

    [Fact]
    public void The_first_import_into_an_empty_save_flags_nothing()
    {
        using var save = new TempSave();

        var preview = save.Runner.Preview(Fixtures.Directory("group-and-dm"));

        Assert.False(preview.AccountIsNewToOwner);
        Assert.Null(preview.OwnerName);
    }

    private static string Export(long ownerId, string messages) => $$"""
        {
          "about": "Test export.",
          "personal_information": { "user_id": {{ownerId}}, "first_name": "Kirill", "last_name": "Chekanov" },
          "chats": { "about": "Chats.", "list": [
            { "name": "Sam Ruiz", "type": "personal_chat", "id": 100, "messages": [ {{messages}} ] }
          ] }
        }
        """;
}
