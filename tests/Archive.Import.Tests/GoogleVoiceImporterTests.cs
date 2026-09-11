using Archive.Import.GoogleVoice;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Google Voice from Takeout: hCard markup, absolute times with offsets, and phone numbers as people.
/// </summary>
public sealed class GoogleVoiceImporterTests
{
    private static RecordingSink Read(string folder, ImportOptions? options = null)
    {
        var sink = new RecordingSink();
        new GoogleVoiceImporter().Read(folder, sink, options);

        return sink;
    }

    private static string Takeout(string name, GoogleVoiceExportBuilder? export = null) =>
        (export ?? Exports.GoogleVoice()).Write(Fixtures.Temp(name));

    [Fact]
    public void A_takeout_is_recognized_and_names_its_number()
    {
        var detection = new GoogleVoiceImporter().Detect(Takeout("gv-detect"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(6, detection.FileCount);
        Assert.Equal(Exports.VoiceOwner, detection.AccountId);
        Assert.False(detection.AccountIdIsGuess);
    }

    [Fact]
    public void Other_exports_are_not_google_voice()
    {
        Assert.Equal(ImportConfidence.None, new GoogleVoiceImporter().Detect(Exports.WriteGroupAndDm("gv-telegram")).Confidence);
        Assert.Equal(ImportConfidence.None, new GoogleVoiceImporter().Detect(Exports.Vk().Write(Fixtures.Temp("gv-vk"))).Confidence);
    }

    /// <summary>A whole Takeout holds three Google exports, and all three are found.</summary>
    [Fact]
    public void A_takeout_root_holds_hangouts_chat_and_voice()
    {
        var root = Fixtures.Temp("gv-root");
        Exports.GoogleVoice().Write(root);
        Exports.GoogleChat().Write(Path.Combine(root, "Takeout"));
        Exports.Hangouts().Write(Path.Combine(root, "Takeout", "Hangouts"));

        Assert.Equal(
            ["hangouts", "googlechat", "googlevoice"],
            new ImporterRegistry().DetectAll(root).Select(m => m.Platform));
    }

    [Fact]
    public void Your_own_messages_are_signed_me_and_are_yours()
    {
        var sink = Read(Takeout("gv-me"));

        Assert.Equal(Exports.VoiceOwner, sink.Owner!.SourceIdentityId);
        Assert.Same(sink.Owner, sink.Messages.Single(m => m.Message.Plaintext == "booked").Message.Sender);
    }

    /// <summary>One person's texts are spread over files, and are still one conversation.</summary>
    [Fact]
    public void Texts_with_one_person_across_files_are_one_conversation()
    {
        var sink = Read(Takeout("gv-thread"));

        var sam = sink.Messages.Where(m => m.Thread.SourceThreadId == $"text/{Exports.VoiceSam.Number}").ToArray();

        // Two files of texts, and two calls with the same number.
        Assert.Equal(5, sam.Length);
        Assert.Equal("Sam Ruiz", sam[0].Thread.Title);
    }

    [Fact]
    public void Times_are_absolute_and_keep_their_offset()
    {
        var first = Read(Takeout("gv-time")).Messages[0].Message;

        Assert.Equal(-240, first.TzOffsetMinutes);
        Assert.Contains(Read(Takeout("gv-time-2")).Messages, m => m.Message.SentAtUnix == 1615757463);
    }

    [Fact]
    public void Line_breaks_in_a_message_survive()
    {
        Assert.Contains(Read(Takeout("gv-br")).Messages, m => m.Message.Plaintext == "first line\nsecond line");
    }

    [Fact]
    public void A_group_text_is_a_group_of_everyone_but_you()
    {
        var group = Read(Takeout("gv-group")).Threads.Single(t => t.Kind == "group");

        Assert.Equal($"group/{Exports.VoiceSam.Number},{Exports.VoiceAlex.Number}", group.SourceThreadId);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void Calls_are_service_messages_and_a_voicemail_carries_its_transcript()
    {
        var messages = Read(Takeout("gv-calls")).Messages.Select(m => m.Message).ToArray();

        Assert.Contains(messages, m => m.ServiceAction == "phone_call" && m.Sender!.SourceIdentityId == Exports.VoiceSam.Number);
        Assert.Contains(messages, m => m.ServiceAction == "missed_call");

        var voicemail = messages.Single(m => m.Plaintext == "call me back about the tickets");
        Assert.Equal("message", voicemail.Kind);
        Assert.Equal("voice", Assert.Single(voicemail.Media).MediaKind);
        Assert.Equal(12, voicemail.Media[0].DurationSeconds);
    }

    [Fact]
    public void Pictures_and_voicemail_audio_are_stored()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.GoogleVoice().Write(save.ExportFolder("gv")));

        Assert.Equal(8, stats.MessagesInserted);
        Assert.Equal(2, stats.MediaStored);
        Assert.Equal(0, stats.MediaNotFound);
    }

    [Fact]
    public void A_file_of_a_kind_nobody_knows_stops_the_import()
    {
        var folder = Takeout("gv-unknown", GoogleVoiceExportBuilder.New(Exports.VoiceOwner)
            .Call(Exports.VoiceSam, "Hologram", DateTimeOffset.FromUnixTimeSeconds(1615757463), TimeSpan.Zero));

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("Hologram", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_owner_can_be_told_and_is_matched_by_number()
    {
        var owner = Read(Takeout("gv-told"), new ImportOptions("+1 (555) 000-0000")).Owner!;

        Assert.Equal(Exports.VoiceOwner, owner.SourceIdentityId);
    }

    [Fact]
    public void Uids_are_stable_across_reads()
    {
        var folder = Takeout("gv-uids");

        Assert.Equal(
            Read(folder).Messages.Select(m => m.Message.Uid),
            Read(folder).Messages.Select(m => m.Message.Uid));
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Exports.GoogleVoice().Write(save.ExportFolder("gv"));

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }
}
