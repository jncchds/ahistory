using Archive.Import.Slack;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Slack's workspace export: real ids, markup that has to be resolved, and no owner.
/// </summary>
public sealed class SlackImporterTests
{
    private static RecordingSink Read(string folder, ImportOptions? options = null)
    {
        var sink = new RecordingSink();
        new SlackImporter().Read(folder, sink, options ?? new ImportOptions("owner"));

        return sink;
    }

    private static string Export(string name, SlackExportBuilder? export = null) =>
        (export ?? Exports.Slack()).Write(Fixtures.Temp(name));

    private static NormalizedMessage Message(RecordingSink sink, string ts) =>
        sink.Messages.Single(m => m.Message.Uid.EndsWith("/" + ts, StringComparison.Ordinal)).Message;

    [Fact]
    public void An_export_is_recognized_and_asks_whose_it_is()
    {
        var detection = new SlackImporter().Detect(Export("slack-detect"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.True(detection.AccountIdIsGuess);
        Assert.Equal(4, detection.FileCount);
        Assert.Contains("owner", detection.Candidates);
    }

    [Fact]
    public void Other_exports_are_not_slack()
    {
        Assert.Equal(ImportConfidence.None, new SlackImporter().Detect(Exports.WriteGroupAndDm("slack-telegram")).Confidence);
        Assert.Equal(ImportConfidence.None, new SlackImporter().Detect(Exports.DiscordPackage().Write(Fixtures.Temp("slack-discord"))).Confidence);
    }

    [Fact]
    public void Every_conversation_and_message_is_read()
    {
        var sink = Read(Export("slack-all"));

        Assert.Equal(3, sink.Threads.Count);
        Assert.Equal(8, sink.Messages.Count);
    }

    /// <summary>Mentions, channel links, links and entities, as Slack showed them.</summary>
    [Fact]
    public void Markup_reads_the_way_it_did_in_slack()
    {
        var sink = Read(Export("slack-markup"));

        Assert.Equal("the harbour was freezing & the map", Message(sink, "1615757463.000100").Plaintext);
        Assert.Equal("@Sam Ruiz we should go back", Message(sink, "1615757523.000200").Plaintext);
        Assert.Equal("deployed to #general", Message(sink, "1615757700.000500").Plaintext);
    }

    [Theory]
    [InlineData("<@U0SAM|sammy> hi", "@sammy hi")]
    [InlineData("<!here> lunch", "@here lunch")]
    [InlineData("<mailto:a@example.org|a@example.org>", "a@example.org")]
    [InlineData("<https://example.org>", "https://example.org")]
    [InlineData("1 &lt; 2", "1 < 2")]
    public void Markup_is_rendered_by_the_rules(string text, string expected) =>
        Assert.Equal(expected, SlackImporter.Render(
            text,
            new Dictionary<string, NormalizedIdentity>(),
            new Dictionary<string, string?>()));

    [Fact]
    public void A_thread_reply_points_at_its_parent()
    {
        var reply = Message(Read(Export("slack-thread")), "1615757523.000200");

        Assert.Equal("sl/C0GENERAL/1615757463.000100", reply.ReplyToUid);
    }

    [Fact]
    public void Joins_are_service_messages_and_bots_are_senders()
    {
        var sink = Read(Export("slack-service"));

        Assert.Equal("invite_members", Message(sink, "1615757583.000300").ServiceAction);

        var bot = Message(sink, "1615757700.000500").Sender!;
        Assert.Equal("bot:B0DEPLOY", bot.SourceIdentityId);
        Assert.Equal("Deploy Bot", bot.DisplayName);
    }

    [Fact]
    public void A_subtype_nobody_knows_stops_the_import()
    {
        var folder = Export("slack-unknown", SlackExportBuilder.New([Exports.SlackSam])
            .Channel("C1", "general", [Exports.SlackSam.Id], c => c
                .Subtype("1615757463.000100", Exports.SlackSam.Id, "hologram_projected", "?")));

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("hologram_projected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reactions_keep_their_named_reactors_and_the_rest_as_a_count()
    {
        var reactions = Message(Read(Export("slack-reactions")), "1615843863.000400").Reactions;

        Assert.Equal([":thumbsup:", ":thumbsup:", ":thumbsup:"], reactions.Select(r => r.Emoji));
        Assert.Equal("Owner Synthetic", reactions[0].ActorIdentity!.DisplayName);
        Assert.Null(reactions[2].ActorIdentity);
        Assert.Equal(1, reactions[2].Count);
    }

    [Fact]
    public void An_edit_keeps_its_time()
    {
        var edited = Message(Read(Export("slack-edit")), "1615757660.000200");

        Assert.StartsWith("2021-03-14T21:35:00", edited.EditedAtUtc, StringComparison.Ordinal);
    }

    [Fact]
    public void Conversations_are_titled_by_channel_and_by_the_other_people()
    {
        var threads = Read(Export("slack-titles")).Threads;

        Assert.Equal("#general", threads.Single(t => t.SourceThreadId == "C0GENERAL").Title);
        Assert.Equal("Sam Ruiz", threads.Single(t => t.SourceThreadId == "D0SAM").Title);
        Assert.Equal("Sam Ruiz, Alex Novak", threads.Single(t => t.SourceThreadId == "G0TRIP").Title);
    }

    [Fact]
    public void The_owner_is_whoever_the_user_names_or_the_one_person_in_every_dm()
    {
        var folder = Export("slack-owner");

        Assert.Null(Read(folder, ImportOptions.Default).Owner);
        Assert.Equal("U0OWNER", Read(folder, new ImportOptions("Owner Synthetic")).Owner!.SourceIdentityId);

        var two = Export("slack-owner-inferred", SlackExportBuilder.New([Exports.SlackOwner, Exports.SlackSam, Exports.SlackAlex])
            .Dm("D1", [Exports.SlackOwner.Id, Exports.SlackSam.Id], c => c.Message("1615757463.000100", Exports.SlackSam.Id, "hi"))
            .Dm("D2", [Exports.SlackOwner.Id, Exports.SlackAlex.Id], c => c.Message("1615757463.000100", Exports.SlackAlex.Id, "hi")));

        Assert.Equal("U0OWNER", Read(two, ImportOptions.Default).Owner!.SourceIdentityId);
        Assert.Equal("U0OWNER", new SlackImporter().Detect(two).AccountId);
    }

    [Fact]
    public void Shared_files_are_recorded_by_name()
    {
        using var save = new TempSave();

        var stats = save.Runner.Run(Exports.Slack().Write(save.ExportFolder("slack")), ownerAccountId: "owner");

        Assert.Equal(8, stats.MessagesInserted);
        Assert.Equal(1, stats.MediaMissing);
        Assert.Equal("harbour.jpg", save.Scalar<string>("SELECT original_filename FROM message_media;"));
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Exports.Slack().Write(save.ExportFolder("slack"));

        save.Runner.Run(folder, ownerAccountId: "owner");
        var before = save.Digest();

        var second = save.Runner.Run(folder, ownerAccountId: "owner");

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }
}
