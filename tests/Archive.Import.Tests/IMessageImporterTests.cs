using Archive.Import.IMessage;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// The Messages database a Mac keeps: text in two different columns, Apple's own epoch, tapbacks
/// that arrive as messages, and a file that says nothing about whose Mac it is.
/// </summary>
/// <remarks>
/// These prove the reader does what was intended. They cannot prove the intention matches a real
/// chat.db — the builder and the reader were written from the same reading of the schema, which is
/// exactly the agreement D22 warns is worth nothing on its own.
/// </remarks>
public sealed class IMessageImporterTests
{
    private static readonly DateTimeOffset At = new(2021, 3, 14, 22, 41, 3, TimeSpan.Zero);

    private const string Sam = "+15551234567";
    private const string Alex = "alex@example.org";

    private static RecordingSink Read(string folder, ImportOptions? options = null)
    {
        var sink = new RecordingSink();
        new IMessageImporter().Read(folder, sink, options);

        return sink;
    }

    /// <summary>One conversation, one group, and a tapback on the first message.</summary>
    private static IMessageDatabaseBuilder Messages() =>
        IMessageDatabaseBuilder.New()
            .Chat(1, "iMessage;-;+15551234567", Sam, "Sam Ruiz")
            .Group(2, "iMessage;+;chat9001", "Prague trip", Sam, Alex)
            .Message(1, "m-1", At, fromMe: false, "the harbour was freezing")
            .Attributed(1, "m-2", At.AddMinutes(1), fromMe: true, "we should go back")
            .Tapback(1, "m-3", At.AddMinutes(2), fromMe: true, 2000, "m-1")
            .Attributed(2, "m-4", At.AddMinutes(3), fromMe: false, "поезд в 07:40", Alex);

    [Fact]
    public void A_messages_database_is_recognized_and_asks_whose_it_is()
    {
        var detection = new IMessageImporter().Detect(Messages().Write(Fixtures.Temp("imessage-detect")));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.True(detection.AccountIdIsGuess);
        Assert.Contains("Full Disk Access", detection.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_sqlite_file_called_something_else_is_not_one()
    {
        var folder = Fixtures.Temp("imessage-not-mine");
        File.WriteAllText(Path.Combine(folder, "notes.db"), "not a database at all");

        Assert.Equal(ImportConfidence.None, new IMessageImporter().Detect(folder).Confidence);
    }

    [Fact]
    public void A_telegram_export_is_not_mistaken_for_one() =>
        Assert.Equal(
            ImportConfidence.None,
            new IMessageImporter().Detect(Exports.WriteGroupAndDm("imessage-not-telegram")).Confidence);

    /// <summary>
    /// Since High Sierra the words are usually not in the text column at all.
    /// </summary>
    [Fact]
    public void Text_is_read_from_either_column()
    {
        var messages = Read(Messages().Write(Fixtures.Temp("imessage-text"))).Messages;

        Assert.Contains(messages, m => m.Message.Plaintext == "the harbour was freezing");
        Assert.Contains(messages, m => m.Message.Plaintext == "we should go back");
        Assert.Contains(messages, m => m.Message.Plaintext == "поезд в 07:40");
    }

    /// <summary>An archived string this reader does not understand stops the import (D20).</summary>
    [Fact]
    public void An_attributed_body_of_an_unknown_shape_is_refused()
    {
        var blob = IMessageDatabaseBuilder.TypedStream("hello");

        // The byte after NSString is the typedstream version marker; changing it makes this a shape
        // the reader has never seen.
        var index = Array.IndexOf(blob, (byte)'g', blob.Length - 12) + 1;
        blob[index] = 0x99;

        var error = Assert.Throws<InvalidDataException>(() => TypedStreamText.Read(blob));

        Assert.Contains("does not know that shape", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blob_with_no_string_in_it_is_an_empty_message_rather_than_a_failure() =>
        Assert.Null(TypedStreamText.Read("streamtyped nonsense"u8.ToArray()));

    /// <summary>
    /// Read as Unix seconds, the whole archive lands in 1970; read as milliseconds it lands
    /// centuries out.
    /// </summary>
    [Fact]
    public void Apple_timestamps_are_read_in_both_units()
    {
        var nanoseconds = IMessageImporter.When(637454463L * 1_000_000_000L);
        var seconds = IMessageImporter.When(637454463L);

        Assert.Equal(nanoseconds, seconds);
        Assert.Equal(2021, nanoseconds.Year);
    }

    [Fact]
    public void A_date_that_is_neither_is_refused() =>
        Assert.Throws<InvalidDataException>(() => IMessageImporter.When(long.MaxValue / 2));

    /// <summary>
    /// A tapback is a row of its own. Imported as a message, a conversation fills up with "Loved an
    /// image"; folded in, it is what it was.
    /// </summary>
    [Fact]
    public void A_tapback_is_a_reaction_and_not_a_message()
    {
        var messages = Read(Messages().Write(Fixtures.Temp("imessage-tapback"))).Messages;

        Assert.Equal(3, messages.Count);

        var loved = messages.Single(m => m.Message.Uid.EndsWith("/m-1", StringComparison.Ordinal));
        var reaction = Assert.Single(loved.Message.Reactions);

        Assert.Equal("❤️", reaction.Emoji);
        Assert.Equal(1, reaction.Count);
    }

    /// <summary>Taking a tapback back is a row too, and it cancels the one it names.</summary>
    [Fact]
    public void A_tapback_that_was_taken_back_is_gone()
    {
        var folder = IMessageDatabaseBuilder.New()
            .Chat(1, "iMessage;-;+15551234567", Sam)
            .Message(1, "m-1", At, fromMe: false, "the harbour was freezing")
            .Tapback(1, "m-2", At.AddMinutes(1), fromMe: true, 2000, "m-1")
            .Tapback(1, "m-3", At.AddMinutes(2), fromMe: true, 3000, "m-1")
            .Write(Fixtures.Temp("imessage-untapback"));

        Assert.Empty(Read(folder).Messages.Single().Message.Reactions);
    }

    [Fact]
    public void Your_own_messages_are_yours_and_theirs_are_theirs()
    {
        var messages = Read(Messages().Write(Fixtures.Temp("imessage-sides"))).Messages
            .ToDictionary(m => m.Message.Uid, m => m.Message.Sender!);

        Assert.Equal(Sam, messages["im/iMessage;-;+15551234567/m-1"].SourceIdentityId);
        Assert.Equal("+15550000000", messages["im/iMessage;-;+15551234567/m-2"].SourceIdentityId);
        Assert.Equal(Alex, messages["im/iMessage;+;chat9001/m-4"].SourceIdentityId);
    }

    /// <summary>The account is written on the owner's own rows, prefixed by what kind it is.</summary>
    [Fact]
    public void The_owner_is_read_from_their_own_messages()
    {
        var owner = Read(Messages().Write(Fixtures.Temp("imessage-owner"))).Owner!;

        Assert.Equal("+15550000000", owner.SourceIdentityId);
        Assert.False(owner.IsSynthetic);
    }

    [Fact]
    public void Without_any_of_your_own_messages_the_owner_is_a_placeholder_until_you_say()
    {
        var folder = IMessageDatabaseBuilder.New()
            .Chat(1, "iMessage;-;+15551234567", Sam)
            .Message(1, "m-1", At, fromMe: false, "hello")
            .Write(Fixtures.Temp("imessage-noowner"));

        Assert.True(Read(folder).Owner!.IsSynthetic);

        var told = Read(folder, new ImportOptions("p:+1 (555) 000-0000")).Owner!;

        Assert.Equal("+15550000000", told.SourceIdentityId);
        Assert.False(told.IsSynthetic);
    }

    /// <summary>D28: a group holds everyone who is in it, whether or not they ever spoke.</summary>
    [Fact]
    public void A_group_holds_everyone_in_the_room()
    {
        var group = Read(Messages().Write(Fixtures.Temp("imessage-group"))).Messages
            .First(m => m.Thread.Kind == "group");

        Assert.Equal("Prague trip", group.Thread.Title);
        Assert.Equal([Sam, Alex], group.Thread.Members.Select(p => p.SourceIdentityId));
    }

    [Fact]
    public void An_attachment_is_found_beside_the_database()
    {
        using var save = new TempSave();

        var folder = IMessageDatabaseBuilder.New()
            .Chat(1, "iMessage;-;+15551234567", Sam)
            .Message(1, "m-1", At, fromMe: false, "look")
            .Attachment("IMG_0001.jpeg", "image/jpeg")
            .Write(save.ExportFolder("imessage"));

        var stats = save.Import(folder);

        Assert.Equal(1, stats.MessagesInserted);
        Assert.Equal(1, stats.MediaStored);
        Assert.Equal("photo", save.Scalar<string>("SELECT media_kind FROM media;"));
    }

    [Fact]
    public void An_attachment_that_was_never_copied_across_is_recorded_as_missing()
    {
        using var save = new TempSave();

        var folder = IMessageDatabaseBuilder.New()
            .Chat(1, "iMessage;-;+15551234567", Sam)
            .Message(1, "m-1", At, fromMe: false, "look")
            .Attachment("IMG_0002.jpeg", "image/jpeg")
            .Write(save.ExportFolder("imessage-missing"));

        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "Attachments"), "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        save.Import(folder);

        Assert.Equal(
            "not in the Attachments folder",
            save.Scalar<string>("SELECT missing_reason FROM message_media;"));
    }

    /// <summary>An event is an event, not something somebody said (§2).</summary>
    [Fact]
    public void A_group_rename_is_a_service_message()
    {
        var folder = IMessageDatabaseBuilder.New()
            .Group(1, "iMessage;+;chat9001", "Prague trip", Sam)
            .Event(1, "m-1", At, itemType: 2, Sam)
            .Write(Fixtures.Temp("imessage-event"));

        var message = Read(folder).Messages.Single().Message;

        Assert.Equal("service", message.Kind);
        Assert.Equal("group_name_changed", message.ServiceAction);
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Messages().Write(save.ExportFolder("imessage"));

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
        Assert.Equal(second.MessagesSeen, second.MessagesSkipped);
    }

    /// <summary>
    /// The same number written two ways is one person, and an Apple ID is one whatever its case.
    /// </summary>
    [Theory]
    [InlineData("p:+1 (555) 123-4567", "+15551234567")]
    [InlineData("e:Someone@Example.org", "someone@example.org")]
    [InlineData("+1 555 123 4567", "+15551234567")]
    public void Accounts_and_handles_land_on_one_identity(string written, string expected) =>
        Assert.Equal(expected, IMessageImporter.Account(written));
}
