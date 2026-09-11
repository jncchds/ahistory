using Archive.Import.Synthetic;
using Archive.Import.WhatsApp;

namespace Archive.Import.Tests;

/// <summary>
/// WhatsApp's chat export: locale dates, no zone, no ids, and the phone's invisible marks.
/// </summary>
public sealed class WhatsAppImporterTests
{
    private static RecordingSink Read(string folder, ImportOptions? options = null)
    {
        var sink = new RecordingSink();
        new WhatsAppImporter().Read(folder, sink, options);

        return sink;
    }

    private static string Chat(string name, params WhatsAppChatBuilder[] chats)
    {
        var folder = Fixtures.Temp(name);

        foreach (var chat in chats)
        {
            chat.Write(folder);
        }

        return folder;
    }

    private static long Unix(DateTime wallClock) =>
        new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void Both_phones_exports_are_recognized()
    {
        var folder = Chat("wa-detect", Exports.WhatsAppIos(), Exports.WhatsAppAndroid());

        var detection = new WhatsAppImporter().Detect(folder);

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(2, detection.FileCount);
        Assert.True(detection.AccountIdIsGuess);
        Assert.Equal("Owner Synthetic", detection.Candidates[0]);
    }

    [Fact]
    public void A_text_file_that_is_not_a_chat_is_not_one()
    {
        var folder = Fixtures.Temp("wa-not");
        File.WriteAllText(Path.Combine(folder, "WhatsApp notes.txt"), "remember to export the chats\n");

        Assert.Equal(ImportConfidence.None, new WhatsAppImporter().Detect(folder).Confidence);
        Assert.Equal(ImportConfidence.None, new WhatsAppImporter().Detect(Exports.WriteGroupAndDm("wa-telegram")).Confidence);
    }

    [Fact]
    public void An_iphone_export_is_read()
    {
        var sink = Read(Chat("wa-ios", Exports.WhatsAppIos()));

        var thread = Assert.Single(sink.Threads);
        Assert.Equal("Sam Ruiz", thread.Title);
        Assert.Equal("dm", thread.Kind);

        var messages = sink.Messages.Select(m => m.Message).ToArray();

        Assert.Equal(5, messages.Length);
        Assert.Equal(Unix(Exports.WhatsAppStart), messages[0].SentAtUnix);
        Assert.Equal("first line\nsecond line", messages[2].Plaintext);
        Assert.Equal("мы были в Праге весной 🌷", messages[4].Plaintext);
        Assert.Equal(Unix(Exports.WhatsAppStart.AddDays(20)), messages[4].SentAtUnix);
    }

    /// <summary>Month first, a two-digit year, a 12-hour clock with a narrow no-break space before PM.</summary>
    [Fact]
    public void An_android_export_is_read()
    {
        var messages = Read(Chat("wa-android", Exports.WhatsAppAndroid())).Messages.Select(m => m.Message).ToArray();

        Assert.Equal(5, messages.Length);

        // Seconds are not in an Android export; the minute is.
        Assert.Equal(Unix(Exports.WhatsAppStart.AddSeconds(-3).AddMinutes(1)), messages[1].SentAtUnix);
        Assert.Equal("who's this", messages[1].Plaintext);
    }

    [Fact]
    public void Notices_are_service_messages_with_nobody_as_sender()
    {
        foreach (var chat in new[] { Exports.WhatsAppIos(), Exports.WhatsAppAndroid() })
        {
            var first = Read(Chat("wa-notice", chat)).Messages[0].Message;

            Assert.Equal("service", first.Kind);
            Assert.Null(first.Sender);
            Assert.StartsWith("Messages and calls are end-to-end encrypted.", first.Plaintext, StringComparison.Ordinal);
        }
    }

    /// <summary>A sender the phone had no contact for is keyed by number, the same way SMS keys them.</summary>
    [Fact]
    public void A_sender_known_only_by_number_is_keyed_by_the_number()
    {
        var sender = Read(Chat("wa-number", Exports.WhatsAppAndroid())).Messages[1].Message.Sender!;

        Assert.Equal("+15551234567", sender.SourceIdentityId);
        Assert.False(sender.IsSynthetic);

        var named = Read(Chat("wa-named", Exports.WhatsAppIos())).Messages[1].Message.Sender!;

        Assert.Null(named.SourceIdentityId);
        Assert.True(named.IsSynthetic);
    }

    [Fact]
    public void Attachments_are_found_beside_the_chat_and_stored()
    {
        using var save = new TempSave();

        var folder = save.ExportFolder("wa");
        Exports.WhatsAppIos().Write(folder);
        Exports.WhatsAppAndroid().Write(folder);

        var stats = save.Import(folder);

        Assert.Equal(10, stats.MessagesInserted);
        Assert.Equal(2, stats.MediaStored);
        Assert.Equal(0, stats.MediaNotFound);
        Assert.Equal(1, stats.MediaMissing);
    }

    [Fact]
    public void A_caption_under_an_android_attachment_is_the_messages_text()
    {
        var message = Read(Chat("wa-caption", Exports.WhatsAppAndroid())).Messages[3].Message;

        Assert.Equal("the view", message.Plaintext);
        Assert.Equal("photo", Assert.Single(message.Media).MediaKind);
    }

    /// <summary>A day above twelve decides the order for the whole folder.</summary>
    [Fact]
    public void A_day_above_twelve_decides_the_date_order()
    {
        var at = new DateTime(2021, 4, 3, 10, 0, 0);

        // 03/04 on its own is ambiguous; the 25/04 beside it is not.
        var sink = Read(Chat("wa-order", WhatsAppChatBuilder.New("Sam Ruiz")
            .Message(at, "Sam Ruiz", "one")
            .Message(new DateTime(2021, 4, 25, 10, 0, 0), "Sam Ruiz", "two")));

        Assert.Equal(Unix(at), sink.Messages[0].Message.SentAtUnix);
    }

    /// <summary>
    /// With every day at twelve or below, the order that keeps the chat in sequence is the right
    /// one: the wrong reading jumps back months each time the month turns.
    /// </summary>
    [Fact]
    public void Chronology_decides_when_no_day_is_above_twelve()
    {
        var dates = new[] { new DateTime(2021, 1, 2, 9, 0, 0), new DateTime(2021, 1, 5, 9, 0, 0), new DateTime(2021, 2, 3, 9, 0, 0) };

        var chat = WhatsAppChatBuilder.New("Sam Ruiz", WhatsAppStyle.Android, "M/d/yy");

        foreach (var date in dates)
        {
            chat.Message(date, "Sam Ruiz", "hi");
        }

        var read = Read(Chat("wa-chronology", chat)).Messages.Select(m => m.Message.SentAtUnix);

        Assert.Equal(dates.Select(Unix), read);
    }

    [Fact]
    public void A_chat_that_never_says_its_date_order_is_refused()
    {
        var at = new DateTime(2021, 4, 3, 10, 0, 0);

        var folder = Chat("wa-ambiguous", WhatsAppChatBuilder.New("Sam Ruiz")
            .Message(at, "Sam Ruiz", "one")
            .Message(at.AddMinutes(5), "Sam Ruiz", "two"));

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("day-first and month-first", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotted_dates_are_day_first()
    {
        var at = new DateTime(2021, 4, 3, 10, 0, 0);

        var sink = Read(Chat("wa-dotted", WhatsAppChatBuilder.New("Sam Ruiz", WhatsAppStyle.Android, "dd.MM.yy")
            .Message(at, "Sam Ruiz", "one")));

        Assert.Equal(Unix(at.AddSeconds(0)), sink.Messages[0].Message.SentAtUnix);
    }

    /// <summary>A file named like a chat that does not start like one is not taken for one.</summary>
    [Fact]
    public void A_chat_file_that_does_not_start_like_a_chat_is_not_one()
    {
        var folder = Chat("wa-garbage", WhatsAppChatBuilder.New("Sam Ruiz")
            .Raw("not a message")
            .Message(new DateTime(2021, 4, 25, 10, 0, 0), "Sam Ruiz", "hi"));

        Assert.Equal(ImportConfidence.None, new WhatsAppImporter().Detect(folder).Confidence);
        Assert.Throws<InvalidDataException>(() => Read(folder));
    }

    /// <summary>Two phones, two date orders, one folder: each chat is read in its own.</summary>
    [Fact]
    public void Chats_from_two_phones_are_each_read_in_their_own_date_order()
    {
        var sink = Read(Chat("wa-two-phones", Exports.WhatsAppIos(), Exports.WhatsAppAndroid()));

        Assert.All(
            sink.Messages.GroupBy(m => m.Thread.SourceThreadId),
            chat => Assert.Equal(Unix(Exports.WhatsAppStart.AddSeconds(-Exports.WhatsAppStart.Second))
                                 / 60, chat.First().Message.SentAtUnix / 60));
    }

    /// <summary>A chat too short to say borrows the order of a chat from the same phone.</summary>
    [Fact]
    public void A_short_chat_borrows_the_order_of_its_neighbours()
    {
        var at = new DateTime(2021, 4, 3, 10, 0, 0);

        var sink = Read(Chat("wa-borrow",
            WhatsAppChatBuilder.New("Sam Ruiz").Message(at, "Sam Ruiz", "only the third of April"),
            WhatsAppChatBuilder.New("Alex Novak").Message(new DateTime(2021, 4, 25, 10, 0, 0), "Alex Novak", "the 25th")));

        var sam = sink.Messages.Single(m => m.Thread.Title == "Sam Ruiz").Message;

        Assert.Equal(Unix(at), sam.SentAtUnix);
    }

    [Fact]
    public void The_owner_is_the_one_sender_in_every_chat()
    {
        var owner = Read(Chat("wa-owner", Exports.WhatsAppIos(), Exports.WhatsAppAndroid())).Owner;

        Assert.Equal("Owner Synthetic", owner!.DisplayName);
    }

    [Fact]
    public void With_one_chat_the_owner_is_whoever_the_user_says()
    {
        var folder = Chat("wa-told", Exports.WhatsAppAndroid());

        Assert.Null(Read(folder).Owner);
        Assert.Equal("Owner Synthetic", Read(folder, new ImportOptions("owner synthetic")).Owner!.DisplayName);
        Assert.Equal("+15551234567", Read(folder, new ImportOptions("+15551234567")).Owner!.SourceIdentityId);
    }

    [Fact]
    public void Derived_uids_are_stable_and_keep_repeats_apart()
    {
        WhatsAppChatBuilder Export() => WhatsAppChatBuilder.New("Sam Ruiz")
            .Message(new DateTime(2021, 4, 25, 10, 0, 0), "Sam Ruiz", "ok")
            .Message(new DateTime(2021, 4, 25, 10, 0, 0), "Sam Ruiz", "ok");

        var first = Read(Chat("wa-uid-1", Export())).Messages.Select(m => m.Message.Uid).ToArray();
        var second = Read(Chat("wa-uid-2", Export())).Messages.Select(m => m.Message.Uid).ToArray();

        Assert.Equal(2, first.Distinct().Count());
        Assert.Equal(first, second);
    }

    [Fact]
    public void The_edited_marker_is_not_part_of_the_text()
    {
        var message = Read(Chat("wa-edited", WhatsAppChatBuilder.New("Sam Ruiz")
            .Message(new DateTime(2021, 4, 25, 10, 0, 0), "Sam Ruiz", "train at 07:40 <This message was edited>")))
            .Messages[0].Message;

        Assert.Equal("train at 07:40", message.Plaintext);
    }

    [Fact]
    public void A_chat_with_more_than_two_senders_is_a_group()
    {
        var sink = Read(Chat("wa-group", WhatsAppChatBuilder.New("Prague trip")
            .Notice(new DateTime(2021, 4, 25, 10, 0, 0), "Sam Ruiz created group \"Prague trip\"")
            .Message(new DateTime(2021, 4, 25, 10, 1, 0), "Sam Ruiz", "tickets?")
            .Message(new DateTime(2021, 4, 25, 10, 2, 0), "Марина Коваль", "мы были в Праге весной")
            .Message(new DateTime(2021, 4, 25, 10, 3, 0), "Owner Synthetic", "booked")));

        Assert.Equal("group", Assert.Single(sink.Threads).Kind);
        Assert.Equal("service", sink.Messages[0].Message.Kind);
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = save.ExportFolder("wa");
        Exports.WhatsAppIos().Write(folder);

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }
}
