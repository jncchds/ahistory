using Archive.Import.Meta;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Facebook Messenger and Instagram: one JSON, two platforms, and a format with no ids and
/// double-encoded text.
/// </summary>
public sealed class MetaImporterTests
{
    private static RecordingSink ReadMessenger(string folder, ImportOptions? options = null)
    {
        var sink = new RecordingSink();
        new MessengerImporter().Read(folder, sink, options);

        return sink;
    }

    private static string Download(string name, MetaExportBuilder export) => export.Write(Fixtures.Temp(name));

    [Fact]
    public void A_facebook_download_is_messenger_and_not_instagram()
    {
        var folder = Download("meta-fb", Exports.Messenger());

        var messenger = new MessengerImporter().Detect(folder);

        Assert.Equal(ImportConfidence.Certain, messenger.Confidence);
        Assert.Equal(2, messenger.FileCount);
        Assert.Equal("Owner Synthetic", messenger.AccountName);
        Assert.False(messenger.AccountIdIsGuess);

        Assert.Equal(ImportConfidence.None, new InstagramImporter().Detect(folder).Confidence);
    }

    [Fact]
    public void An_instagram_download_is_instagram_and_not_messenger()
    {
        foreach (var layout in new[] { MetaLayout.Current, MetaLayout.Legacy })
        {
            var folder = Download($"meta-ig-{layout}", Exports.Instagram(layout));

            Assert.Equal(ImportConfidence.Certain, new InstagramImporter().Detect(folder).Confidence);
            Assert.Equal(ImportConfidence.None, new MessengerImporter().Detect(folder).Confidence);
        }
    }

    [Fact]
    public void The_older_facebook_layout_is_read_too()
    {
        var folder = Download("meta-fb-legacy", Exports.Messenger(MetaLayout.Legacy));

        Assert.Equal(ImportConfidence.Certain, new MessengerImporter().Detect(folder).Confidence);
        Assert.Equal(4, ReadMessenger(folder).Messages.Count);
    }

    /// <summary>
    /// A bare messages folder with nothing marking its product is Messenger, but only at Possible,
    /// and never Instagram — a name there is a different account.
    /// </summary>
    [Fact]
    public void An_unmarked_messages_folder_is_only_possibly_messenger()
    {
        // Moved out on its own: beside the download's profile files it would be marked.
        var folder = Download("meta-bare", Exports.Messenger(MetaLayout.Legacy));
        var messages = Path.Combine(Fixtures.Temp("meta-bare-alone"), "messages");
        Directory.Move(Path.Combine(folder, "messages"), messages);

        Assert.Equal(ImportConfidence.Possible, new MessengerImporter().Detect(messages).Confidence);
        Assert.Equal(ImportConfidence.None, new InstagramImporter().Detect(messages).Confidence);
    }

    [Fact]
    public void A_download_holding_both_products_is_both()
    {
        var root = Fixtures.Temp("meta-both");
        Exports.Messenger().Write(root);
        Exports.Instagram().Write(root);

        Assert.Equal(
            ["messenger", "instagram"],
            new ImporterRegistry().DetectAll(root).Select(m => m.Platform));
    }

    [Fact]
    public void A_telegram_export_is_neither()
    {
        var folder = Exports.WriteGroupAndDm("meta-not-telegram");

        Assert.Equal(ImportConfidence.None, new MessengerImporter().Detect(folder).Confidence);
        Assert.Equal(ImportConfidence.None, new InstagramImporter().Detect(folder).Confidence);
    }

    /// <summary>
    /// Meta writes each UTF-8 byte as its own escape. Unrepaired, every Cyrillic name and every
    /// emoji in the archive is mojibake.
    /// </summary>
    [Fact]
    public void Double_encoded_text_is_repaired()
    {
        var sink = ReadMessenger(Download("meta-mojibake", Exports.Messenger()));

        var message = sink.Messages.Single(m => m.Thread.Kind == "group").Message;

        Assert.Equal("мы были в Праге весной 🌷", message.Plaintext);
        Assert.Equal("Марина Коваль", message.Sender!.DisplayName);
        Assert.Equal("❤", message.Reactions.Single().Emoji);
        Assert.Equal("Sam Ruiz", message.Reactions.Single().ActorIdentity!.DisplayName);
    }

    /// <summary>A string that was never broken is left alone, even when it is not ASCII.</summary>
    [Theory]
    [InlineData("plain ascii")]
    [InlineData("café")]
    [InlineData("Марина")]
    [InlineData("🌷")]
    public void Repair_leaves_correct_text_alone(string text) =>
        Assert.Equal(text, MetaMessagesReader.Repair(text));

    [Fact]
    public void Repair_undoes_the_double_encoding() =>
        Assert.Equal("Марина 🌷", MetaMessagesReader.Repair(MetaExportBuilder.Mangle("Марина 🌷")));

    /// <summary>Files, and the messages in them, run newest first. The archive reads oldest first.</summary>
    [Fact]
    public void Messages_split_across_files_come_out_oldest_first()
    {
        var folder = Download("meta-split", MetaExportBuilder.Facebook("Owner Synthetic")
            .Thread("sam_1", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .Message("Sam Ruiz", DateTimeOffset.FromUnixTimeSeconds(1000), "one")
                .Message("Owner Synthetic", DateTimeOffset.FromUnixTimeSeconds(2000), "two")
                .Message("Sam Ruiz", DateTimeOffset.FromUnixTimeSeconds(3000), "three"), perFile: 2));

        Assert.Equal(["one", "two", "three"], ReadMessenger(folder).Messages.Select(m => m.Message.Plaintext));
    }

    [Fact]
    public void The_owner_is_read_from_the_profile()
    {
        var owner = ReadMessenger(Download("meta-owner", Exports.Messenger())).Owner;

        Assert.Equal("Owner Synthetic", owner!.DisplayName);
    }

    [Fact]
    public void Without_a_profile_the_owner_is_the_one_person_in_every_thread()
    {
        var folder = Download("meta-infer", MetaExportBuilder.Facebook(null)
            .Thread("sam_1", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .Message("Sam Ruiz", DateTimeOffset.FromUnixTimeSeconds(1000), "hi"))
            .Thread("marina_2", "Марина Коваль", ["Owner Synthetic", "Марина Коваль"], t => t
                .Message("Марина Коваль", DateTimeOffset.FromUnixTimeSeconds(2000), "привет")));

        Assert.Equal("Owner Synthetic", ReadMessenger(folder).Owner!.DisplayName);

        // Sam is in both threads of the standard download as well, so there nothing is claimed.
        Assert.Null(ReadMessenger(Download("meta-infer-ambiguous", Exports.Messenger(owner: null))).Owner);
    }

    /// <summary>With one thread both people are in every thread; nothing is claimed, and the preview asks.</summary>
    [Fact]
    public void One_thread_and_no_profile_names_no_owner_and_offers_candidates()
    {
        var folder = Download("meta-ask", MetaExportBuilder.Facebook(null)
            .Thread("sam_1", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .Message("Sam Ruiz", DateTimeOffset.FromUnixTimeSeconds(1000), "hi")));

        Assert.Null(ReadMessenger(folder).Owner);

        var detection = new MessengerImporter().Detect(folder);

        Assert.True(detection.AccountIdIsGuess);
        Assert.Equal(["Owner Synthetic", "Sam Ruiz"], detection.Candidates);

        var told = ReadMessenger(folder, new ImportOptions("Owner Synthetic")).Owner;

        Assert.Equal("Owner Synthetic", told!.DisplayName);
    }

    [Fact]
    public void Calls_become_service_messages()
    {
        var call = ReadMessenger(Download("meta-call", Exports.Messenger())).Messages
            .Single(m => m.Message.Kind == "service").Message;

        Assert.Equal("phone_call", call.ServiceAction);
    }

    [Fact]
    public void A_message_type_nobody_knows_stops_the_import()
    {
        var folder = Download("meta-unknown", MetaExportBuilder.Facebook("Owner Synthetic")
            .Thread("sam_1", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .OfType("Sam Ruiz", DateTimeOffset.FromUnixTimeSeconds(1000), "Hologram")));

        var error = Assert.Throws<InvalidDataException>(() => ReadMessenger(folder));

        Assert.Contains("Hologram", error.Message, StringComparison.Ordinal);
    }

    /// <summary>No ids, so uids are derived — and two identical messages in one millisecond stay two.</summary>
    [Fact]
    public void Derived_uids_are_stable_and_keep_repeats_apart()
    {
        MetaExportBuilder Export() => MetaExportBuilder.Facebook("Owner Synthetic")
            .Thread("sam_1", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .Message("Sam Ruiz", DateTimeOffset.FromUnixTimeMilliseconds(1000), "ok")
                .Message("Sam Ruiz", DateTimeOffset.FromUnixTimeMilliseconds(1000), "ok"));

        var first = ReadMessenger(Download("meta-uid-1", Export())).Messages.Select(m => m.Message.Uid).ToArray();
        var second = ReadMessenger(Download("meta-uid-2", Export())).Messages.Select(m => m.Message.Uid).ToArray();

        Assert.Equal(2, first.Distinct().Count());
        Assert.Equal(first, second);
    }

    /// <summary>A thread archived between two downloads is still the same thread.</summary>
    [Fact]
    public void A_thread_that_moved_to_the_archive_keeps_its_id()
    {
        MetaExportBuilder Export(string box) => MetaExportBuilder.Facebook("Owner Synthetic")
            .Thread("sam_1", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .Message("Sam Ruiz", DateTimeOffset.FromUnixTimeSeconds(1000), "hi"), box: box);

        var inbox = ReadMessenger(Download("meta-inbox", Export("inbox"))).Messages.Single().Message;
        var archived = ReadMessenger(Download("meta-archived", Export("archived_threads"))).Messages.Single().Message;

        Assert.Equal(inbox.Uid, archived.Uid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Photos_are_found_whether_the_root_or_the_messages_folder_was_chosen(bool pointAtMessages)
    {
        using var save = new TempSave();

        var root = Exports.Messenger().Write(save.ExportFolder("fb"));
        var folder = pointAtMessages ? Path.Combine(root, "your_facebook_activity", "messages") : root;

        var stats = save.Import(folder);

        Assert.Equal(4, stats.MessagesInserted);
        Assert.Equal(1, stats.MediaStored);
        Assert.Equal(0, stats.MediaNotFound);
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Exports.Messenger().Write(save.ExportFolder("fb"));

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }

    /// <summary>The same name on two Meta products is two accounts until someone says otherwise.</summary>
    [Fact]
    public void The_same_name_on_messenger_and_instagram_is_two_accounts()
    {
        using var save = new TempSave();

        save.Import(Exports.Messenger().Write(save.ExportFolder("fb")));
        save.Import(Exports.Instagram().Write(save.ExportFolder("ig")));

        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM identity WHERE display_name = 'Sam Ruiz';"));
    }
}
