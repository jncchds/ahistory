using Archive.Import.Skype;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Skype's export: a strong format with real ids, whose content is markup rather than text.
/// </summary>
public sealed class SkypeImporterTests
{
    private static RecordingSink Read(string folder)
    {
        var sink = new RecordingSink();
        new SkypeImporter().Read(folder, sink);

        return sink;
    }

    private static string Export(string name, SkypeExportBuilder? export = null) =>
        (export ?? Exports.Skype()).Write(Fixtures.Temp(name));

    private static NormalizedMessage Message(RecordingSink sink, string idSuffix) =>
        sink.Messages.Single(m => m.Message.Uid.EndsWith(idSuffix, StringComparison.Ordinal)).Message;

    [Fact]
    public void An_export_is_recognized_and_names_its_account()
    {
        var detection = new SkypeImporter().Detect(Export("skype-detect"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(Exports.SkypeOwner, detection.AccountId);
        Assert.False(detection.AccountIdIsGuess);
    }

    [Fact]
    public void Other_exports_with_a_messages_json_are_not_skype()
    {
        Assert.Equal(ImportConfidence.None, new SkypeImporter().Detect(Exports.WriteGroupAndDm("skype-telegram")).Confidence);
        Assert.Equal(ImportConfidence.None, new SkypeImporter().Detect(Exports.GoogleChat().Write(Fixtures.Temp("skype-gc"))).Confidence);
    }

    [Fact]
    public void The_owner_is_the_exports_own_user_id()
    {
        var sink = Read(Export("skype-owner"));

        Assert.Equal(Exports.SkypeOwner, sink.Owner!.SourceIdentityId);
        Assert.Same(sink.Owner, Message(sink, "1615757523001").Sender);
    }

    [Fact]
    public void Markup_is_read_as_what_a_person_saw()
    {
        var sink = Read(Export("skype-markup"));

        Assert.Equal("the harbour was freezing & windy", Message(sink, "1615757463001").Plaintext);
        Assert.Equal("see https://example.org", Message(sink, "1615757523001").Plaintext);
        Assert.Equal("мы были в Праге весной (heart)", Message(sink, "1615843863001").Plaintext);
    }

    /// <summary>A quote carries a legacy copy of its own header, which is not part of what was said.</summary>
    [Fact]
    public void A_quote_keeps_what_was_quoted_and_drops_its_legacy_header()
    {
        var quote = Message(Read(Export("skype-quote")), "1615843960001");

        Assert.Equal("мы были в Праге весной\nand again", quote.Plaintext);
    }

    [Fact]
    public void An_edit_loses_its_marker_and_keeps_its_time()
    {
        var edited = Message(Read(Export("skype-edit")), "1615757643001");

        Assert.Equal("train at 07:40", edited.Plaintext);
        Assert.StartsWith("2021-03-14T21:35:00", edited.EditedAtUtc, StringComparison.Ordinal);
    }

    [Fact]
    public void Calls_and_members_joining_are_service_messages()
    {
        var sink = Read(Export("skype-service"));

        Assert.Equal("phone_call", Message(sink, "1615757583001").ServiceAction);
        Assert.Equal("invite_members", Message(sink, "1615843900001").ServiceAction);
    }

    /// <summary>Skype's own feeds and its notices are skipped by name, and nothing else is.</summary>
    [Fact]
    public void Call_logs_and_notices_are_skipped()
    {
        var sink = Read(Export("skype-skipped"));

        Assert.Equal(2, sink.Threads.Count);
        Assert.Equal(8, sink.Messages.Count);
        Assert.DoesNotContain(sink.Messages, m => m.Message.Plaintext == "welcome");
    }

    [Fact]
    public void A_message_type_nobody_knows_stops_the_import()
    {
        var folder = Export("skype-unknown", SkypeExportBuilder.New(Exports.SkypeOwner)
            .Conversation(Exports.SkypeSam, "Sam Ruiz", c => c
                .Message("1", DateTimeOffset.FromUnixTimeSeconds(1615757463), Exports.SkypeSam, null, "?", type: "Hologram/Projection")));

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("Hologram/Projection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_conversation_kind_nobody_knows_stops_the_import()
    {
        var folder = Export("skype-kind", SkypeExportBuilder.New(Exports.SkypeOwner)
            .Conversation("99:somewhere", "?", c => c
                .Message("1", DateTimeOffset.FromUnixTimeSeconds(1615757463), Exports.SkypeSam, null, "?")));

        Assert.Throws<InvalidDataException>(() => Read(folder));
    }

    /// <summary>Older exports wrote senders as URLs. Read down to the id, they are the same person.</summary>
    [Fact]
    public void A_sender_written_as_a_url_is_the_same_person()
    {
        var sink = Read(Export("skype-url"));

        Assert.Equal(Exports.SkypeSam, Message(sink, "1615843960001").Sender!.SourceIdentityId);
        Assert.Equal(Exports.SkypeSam, Message(sink, "1615757463001").Sender!.SourceIdentityId);
    }

    [Fact]
    public void A_group_is_named_by_its_topic_and_lists_its_members()
    {
        var group = Read(Export("skype-group")).Threads.Single(t => t.Kind == "group");

        Assert.Equal("Prague trip", group.Title);
        Assert.Equal(3, group.Members.Count);
    }

    [Fact]
    public void A_shared_photo_is_recorded_by_name_without_its_contents()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.Skype().Write(save.ExportFolder("skype")));

        Assert.Equal(8, stats.MessagesInserted);
        Assert.Equal(1, stats.MediaMissing);
        Assert.Equal("IMG_0001.jpg", save.Scalar<string>("SELECT original_filename FROM message_media;"));
    }

    [Fact]
    public void Arrival_times_are_read_exactly()
    {
        var first = Message(Read(Export("skype-time")), "1615757463001");

        Assert.Equal(1615757463, first.SentAtUnix);
    }

    /// <summary>Real ids: a changed message is a revision of the one already stored, not a second one.</summary>
    [Fact]
    public void An_edit_between_exports_is_a_revision()
    {
        using var save = new TempSave();

        var at = DateTimeOffset.FromUnixTimeSeconds(1615757463);

        SkypeExportBuilder Build(string text, DateTimeOffset? edited) => SkypeExportBuilder.New(Exports.SkypeOwner)
            .Conversation(Exports.SkypeSam, "Sam Ruiz", c => c.Message("1", at, Exports.SkypeSam, "Sam Ruiz", text, editedAt: edited));

        save.Import(Build("train at 07:04", null).Write(save.ExportFolder("skype")));
        var second = save.Import(Build("train at 07:40", at.AddMinutes(2)).Write(save.ExportFolder("skype")));

        Assert.Equal(0, second.MessagesInserted);
        Assert.Equal(1, second.MessagesRevised);
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Exports.Skype().Write(save.ExportFolder("skype"));

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }
}
