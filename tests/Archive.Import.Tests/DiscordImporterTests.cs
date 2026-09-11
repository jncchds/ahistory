using Archive.Import.Discord;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Discord's own data package and DiscordChatExporter's JSON: two shapes, one set of ids.
/// </summary>
public sealed class DiscordImporterTests
{
    private static RecordingSink Read(string folder, ImportOptions? options = null)
    {
        var sink = new RecordingSink();
        new DiscordImporter().Read(folder, sink, options);

        return sink;
    }

    private static string Package(string name, DiscordPackageBuilder? package = null) =>
        (package ?? Exports.DiscordPackage()).Write(Fixtures.Temp(name));

    private static string Exported(string name, params DiscordChatExporterBuilder[] exports)
    {
        var folder = Fixtures.Temp(name);

        foreach (var export in exports.Length == 0 ? [Exports.DiscordExport()] : exports)
        {
            export.Write(folder);
        }

        return folder;
    }

    [Fact]
    public void A_package_is_recognized_and_names_its_account()
    {
        var detection = new DiscordImporter().Detect(Package("dc-detect"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(Exports.DiscordOwner.Id, detection.AccountId);
        Assert.Equal("Owner Synthetic", detection.AccountName);
        Assert.Equal(3, detection.FileCount);
        Assert.Contains("only the messages you sent", detection.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exporter_file_is_recognized_and_asks_whose_it_is()
    {
        var detection = new DiscordImporter().Detect(Exported("dc-detect-dce"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.True(detection.AccountIdIsGuess);
    }

    [Fact]
    public void Other_exports_are_not_discord()
    {
        Assert.Equal(ImportConfidence.None, new DiscordImporter().Detect(Exports.WriteGroupAndDm("dc-telegram")).Confidence);
        Assert.Equal(ImportConfidence.None, new DiscordImporter().Detect(Exports.Skype().Write(Fixtures.Temp("dc-skype"))).Confidence);
    }

    /// <summary>The package is only the owner's side, and says so by having nobody else in it.</summary>
    [Fact]
    public void Every_package_message_is_the_owners()
    {
        var sink = Read(Package("dc-owner"));

        Assert.Equal(4, sink.Messages.Count);
        Assert.All(sink.Messages, m => Assert.Equal(Exports.DiscordOwner.Id, m.Message.Sender!.SourceIdentityId));
    }

    /// <summary>A message with a comma, a quote and a newline survives the CSV whole.</summary>
    [Fact]
    public void Quoted_csv_fields_are_read_whole()
    {
        var first = Read(Package("dc-csv")).Messages[0].Message;

        Assert.Equal("the harbour was freezing, really\n\"freezing\"", first.Plaintext);
        Assert.Equal(1615757463, first.SentAtUnix);
    }

    [Theory]
    [InlineData("a,b\n", new[] { "a", "b" })]
    [InlineData("\"a,b\",c\n", new[] { "a,b", "c" })]
    [InlineData("\"say \"\"hi\"\"\",x", new[] { "say \"hi\"", "x" })]
    [InlineData("\"line\r\nbreak\",y\r\n", new[] { "line\nbreak", "y" })]
    public void Csv_rows_are_read_by_the_rules(string csv, string[] expected) =>
        Assert.Equal(expected, Assert.Single(DiscordImporter.Csv(new StringReader(csv))));

    [Fact]
    public void The_newer_json_messages_are_read_too()
    {
        var group = Read(Package("dc-json")).Messages.Single(m => m.Thread.Kind == "group").Message;

        Assert.Equal("мы были в Праге весной 🌷", group.Plaintext);
        Assert.Equal(1615843863, group.SentAtUnix);
    }

    [Fact]
    public void Channels_are_titled_the_way_discord_names_them()
    {
        var threads = Read(Package("dc-titles")).Threads;

        Assert.Equal("sam", threads.Single(t => t.Kind == "dm").Title);
        Assert.Equal("Prague trip", threads.Single(t => t.Kind == "group").Title);
        Assert.Equal("Book Club #general", threads.Single(t => t.Kind == "channel").Title);
        Assert.Equal(3, threads.Single(t => t.Kind == "group").Members.Count);
    }

    [Fact]
    public void A_channel_type_nobody_knows_stops_the_import()
    {
        var folder = Package("dc-type", DiscordPackageBuilder.New(Exports.DiscordOwner)
            .GuildChannel("1", "Server", "odd", c => c.Message("2", DateTimeOffset.FromUnixTimeSeconds(1615757463), "?"), type: 99));

        Assert.Throws<InvalidDataException>(() => Read(folder));
    }

    [Fact]
    public void Package_attachments_are_links_recorded_as_missing()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.DiscordPackage().Write(save.ExportFolder("dc")));

        Assert.Equal(4, stats.MessagesInserted);
        Assert.Equal(1, stats.MediaMissing);
        Assert.Equal("harbour.jpg", save.Scalar<string>("SELECT original_filename FROM message_media;"));
    }

    [Fact]
    public void Exported_authors_are_keyed_by_id_and_shown_by_nickname()
    {
        var reply = Read(Exported("dc-authors")).Messages[1].Message;

        Assert.Equal(Exports.DiscordSam.Id, reply.Sender!.SourceIdentityId);
        Assert.Equal("Sam Ruiz", reply.Sender.DisplayName);
        Assert.Equal("sam", reply.Sender.Handle);
    }

    [Fact]
    public void Replies_reactions_and_calls_are_kept()
    {
        var messages = Read(Exported("dc-rich")).Messages.Select(m => m.Message).ToArray();

        Assert.Equal($"dc/{Exports.DiscordDmChannel}/300000000000000001", messages[1].ReplyToUid);

        // Three reactions, one reactor named: the other two survive as a count.
        var reactions = messages[1].Reactions;
        Assert.Equal(Exports.DiscordOwner.Id, reactions[0].ActorIdentity!.SourceIdentityId);
        Assert.Equal(2, reactions[1].Count);
        Assert.Null(reactions[1].ActorIdentity);

        Assert.Equal("phone_call", messages[3].ServiceAction);
    }

    [Fact]
    public void An_exported_message_type_nobody_knows_stops_the_import()
    {
        var folder = Exported("dc-unknown", DiscordChatExporterBuilder.Dm("1", "sam")
            .Message("2", DateTimeOffset.FromUnixTimeSeconds(1615757463), Exports.DiscordSam, "?", type: "Hologram"));

        Assert.Throws<InvalidDataException>(() => Read(folder));
    }

    /// <summary>In one direct chat, the owner is the author who is not the person it is named after.</summary>
    [Fact]
    public void The_owner_of_a_single_exported_dm_is_the_other_author()
    {
        Assert.Equal(Exports.DiscordOwner.Id, Read(Exported("dc-infer")).Owner!.SourceIdentityId);
    }

    [Fact]
    public void The_owner_can_be_named_by_username()
    {
        var folder = Exported("dc-told", DiscordChatExporterBuilder.Group("5", "Prague trip")
            .Message("6", DateTimeOffset.FromUnixTimeSeconds(1615757463), Exports.DiscordSam, "hi")
            .Message("7", DateTimeOffset.FromUnixTimeSeconds(1615757563), Exports.DiscordOwner, "hello"));

        Assert.Null(Read(folder).Owner);
        Assert.Equal(Exports.DiscordOwner.Id, Read(folder, new ImportOptions("owner")).Owner!.SourceIdentityId);
    }

    [Fact]
    public void Downloaded_media_is_stored_and_linked_media_recorded()
    {
        using var save = new TempSave();

        var folder = save.ExportFolder("dce");
        Exports.DiscordExport().Write(folder);
        DiscordChatExporterBuilder.Dm("8", "alex")
            .Message("9", DateTimeOffset.FromUnixTimeSeconds(1615757463), Exports.DiscordAlex, string.Empty, m => m.Attachment("far.png", seed: null))
            .Write(folder);

        var stats = save.Import(folder);

        Assert.Equal(1, stats.MediaStored);
        Assert.Equal(0, stats.MediaNotFound);
        Assert.Equal(1, stats.MediaMissing);
    }

    /// <summary>
    /// The package's copy of a message and the exporter's copy are one message. Only the ids make
    /// that possible, and it is why the two shapes share a platform.
    /// </summary>
    [Fact]
    public void A_message_in_both_the_package_and_an_export_lands_once()
    {
        using var save = new TempSave();

        save.Import(Exports.DiscordPackage().Write(save.ExportFolder("package")));
        var second = save.Import(Exports.DiscordExport().Write(save.ExportFolder("exported")));

        Assert.Equal(3, second.MessagesInserted);
        Assert.Equal(1, second.MessagesSkipped);
        Assert.Equal(7, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Exports.DiscordPackage().Write(save.ExportFolder("dc"));
        Exports.DiscordExport().Write(folder);

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }
}
