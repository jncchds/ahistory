using System.Text;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// The export shapes the tests share, built when a test runs.
/// </summary>
/// <remarks>
/// <para>
/// These used to be files under <c>tests/fixtures</c>. They are built here instead because an
/// archive is people's private correspondence, and a checked-in file shaped exactly like an export
/// is one careless copy away from being somebody's real one. Nothing that looks like archive data
/// lives in this repository — <c>NoArchiveDataInTheRepositoryTests</c> enforces it.
/// </para>
/// <para>
/// Each shape is still written to a real folder on disk, because reading an export folder is most
/// of what an importer does and a test that handed it a string would not exercise that at all.
/// </para>
/// <para>
/// Every name here is invented. The owner is deliberately not a real person: an owner named after
/// whoever wrote the test is how a fixture starts drifting towards real data.
/// </para>
/// </remarks>
internal static class Exports
{
    internal const long OwnerId = 777001;

    internal static TelegramAccount Owner { get; } = TelegramAccount.User(OwnerId, "Owner Synthetic");

    internal static TelegramAccount Sam { get; } = TelegramAccount.User(5001, "Sam Ruiz");

    internal static TelegramAccount Alex { get; } = TelegramAccount.User(5002, "Alex Novak");

    /// <summary>Central European summer time, so a timestamp carries a non-zero offset.</summary>
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);

    private static DateTimeOffset At(long unix, TimeSpan? offset = null) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(offset ?? TimeSpan.Zero);

    /// <summary>
    /// The workhorse: an owner, a direct chat, a group, and a chat that was left.
    /// </summary>
    /// <remarks>
    /// Small enough to read whole, and between them the four shapes cover most of what the
    /// importer has to keep straight — who "me" is, a reply, a service message, and a section
    /// (left_chats) whose absence would quietly lose whole relationships.
    /// </remarks>
    internal static TelegramExportBuilder GroupAndDm() =>
        TelegramExportBuilder.Full()
            .Owner(OwnerId, "Owner", "Synthetic", username: "owner", phoneNumber: "+10000000000")
            .Chat("Sam Ruiz", "personal_chat", 100, c => c
                .Message(1, At(1554221523, Cest), Sam, "the harbour was freezing")
                .Message(2, At(1554221680, Cest), Owner, "we should go back", m => m.ReplyTo(1)))
            .Chat("Prague trip", "private_group", 200, c => c
                .Service(11, At(1551427200), Owner, "create_group", m => m.Field("title", "Prague trip"))
                .Message(12, At(1551427512), Sam, "yeah exactly"))
            .LeftChat("Old book club", "private_group", 300, c => c
                .Message(21, At(1479676980), Alex, "see you thursday"));

    /// <summary><see cref="GroupAndDm"/> in a throwaway folder, for tests with no save of their own.</summary>
    internal static string WriteGroupAndDm(string name) => GroupAndDm().Write(Fixtures.Temp(name));

    /// <summary>Telegram Desktop exporting one conversation on its own, with no owner block.</summary>
    internal static TelegramExportBuilder SingleChat() =>
        TelegramExportBuilder.OneChat("Sam Ruiz", "personal_chat", 100, c => c
            .Message(1, At(1554221523, Cest), Sam, "exported from one chat only"));

    /// <summary>§2's first trap: <c>text</c> and <c>text_entities</c> deliberately disagree.</summary>
    internal static TelegramExportBuilder TextStringVsEntities() =>
        TelegramExportBuilder.OneChat("Sam Ruiz", "personal_chat", 100, c => c
            .Message(1, At(1554221523, Cest), Sam, string.Empty, m => m
                // "WRONG" so a regression that reads this field produces an obviously wrong value
                // rather than a plausible one.
                .RawTextField("WRONG - this field must never be read")
                .Entities(
                    ("plain", "see "),
                    ("link", "https://example.org"),
                    ("plain", " for the map")))
            .Message(2, At(1554221580, Cest), Sam, string.Empty, m => m
                .RawTextArray("mixed ", ("bold", "array"), " form")
                .Entities(("plain", "mixed "), ("bold", "array"), ("plain", " form"))));

    /// <summary>Service messages, which carry an actor and an action rather than a sender.</summary>
    internal static TelegramExportBuilder ServiceMessages() =>
        TelegramExportBuilder.OneChat("Prague trip", "private_group", 200, c => c
            .Service(1, At(1551427200), Owner, "create_group", m => m.Field("title", "Prague trip"))
            .Service(2, At(1551427260), Owner, "invite_members", m => m
                .Field("members", new System.Text.Json.Nodes.JsonArray("Sam Ruiz")))
            .Service(3, At(1551554000), Sam, "phone_call", m => m
                .Field("duration_seconds", 61)
                .Field("discard_reason", "hangup")));

    internal static TelegramExportBuilder PrefixedIds() =>
        TelegramExportBuilder.OneChat("Mixed senders", "private_supergroup", 400, c => c
            .Message(1, At(1554221523), Sam, "a")
            .Message(2, At(1554221524), TelegramAccount.Channel(900, "Announcements"), "b")
            .Message(3, At(1554221525), TelegramAccount.Unprefixed(5003, "Legacy"), "c"));

    /// <summary>A participant kind that does not exist yet — the import must stop, not guess.</summary>
    internal static TelegramExportBuilder UnknownPrefix() =>
        TelegramExportBuilder.OneChat("Future", "private_group", 500, c => c
            .Message(1, At(1554221523), TelegramAccount.Raw("spaceship42", "Someone"),
                "from a participant kind that does not exist yet"));

    internal static TelegramExportBuilder NameOnlySender() =>
        TelegramExportBuilder.OneChat("Old group", "private_group", 600, c => c
            .Message(1, At(1479676980), TelegramAccount.NameOnly("Deleted Account"), "no id on this one"));

    internal static TelegramExportBuilder SavedMessages() =>
        TelegramExportBuilder.OneChat("Owner Synthetic", "saved_messages", OwnerId, c => c
            .Message(1, At(1554221523), Owner, "flat viewing thursday 18:00"));

    internal static TelegramExportBuilder ReactionsAndEdits() =>
        TelegramExportBuilder.OneChat("Prague trip", "private_group", 200, c => c
            .Message(1, At(1551697200), Sam, "train at 07:40 not 07:04", m => m
                .Edited(At(1551697335))
                // Five reactions, two of them named: Telegram lists only recent reactors, and the
                // remainder has to survive as an anonymous row or the counts stop adding up.
                .Reaction("👍", 5, (Owner, At(1551697380)), (Alex, At(1551697440)))
                .CustomReaction("5379748062124056162", 1, (Alex, At(1551697500)))));

    /// <summary>Attachments the export left out, which are recorded rather than dropped (§2).</summary>
    internal static TelegramExportBuilder MissingMedia() =>
        TelegramExportBuilder.OneChat("Sam Ruiz", "personal_chat", 100, c => c
            .Message(1, At(1622538000), Sam, "the view from the flat", m => m
                .Photo(TelegramMessageBuilder.NotIncluded, width: 1280, height: 960))
            .Message(2, At(1622538060), Sam, string.Empty, m => m
                .File(TelegramMessageBuilder.NotIncluded, "voice_message", "audio/ogg", durationSeconds: 12)));

    internal const string StickerPath = "stickers/sticker.webp";
    internal const string ClipPath = "video_files/clip.mp4";
    internal const string ClipThumbnailPath = "video_files/clip_thumb.jpg";

    /// <summary>
    /// Forwards, a bot, and the same sticker sent twice.
    /// </summary>
    /// <remarks>
    /// The repeated sticker is the point: exports repeat one file hundreds of times, and noticing
    /// that is the media store's whole job (§1). Use <see cref="WriteForwards"/> to get the folder
    /// with the files actually present.
    /// </remarks>
    internal static TelegramExportBuilder Forwards() =>
        TelegramExportBuilder.OneChat("Sam Ruiz", "personal_chat", 100, c => c
            .Message(1, At(1578218400), Sam, "look at this", m => m.ForwardedFrom("Alex Novak"))
            .Message(2, At(1578218430), Sam, string.Empty, m => m.Sticker(StickerPath, "🐢"))
            .Message(3, At(1578218460), Owner, string.Empty, m => m.Sticker(StickerPath, "🐢"))
            .Message(4, At(1578218520), Sam, string.Empty, m => m
                .ViaBot("@gif")
                .File(ClipPath, "animation", "video/mp4", durationSeconds: 4, width: 320, height: 240)
                .Thumbnail(ClipThumbnailPath)));

    /// <summary>The forwards export with its attachments present on disk.</summary>
    internal static string WriteForwards(string folder)
    {
        Forwards().Write(folder);

        SyntheticMedia.Write(folder, StickerPath, seed: 1);
        SyntheticMedia.Write(folder, ClipPath, seed: 2);
        SyntheticMedia.Write(folder, ClipThumbnailPath, seed: 3);

        return folder;
    }

    /// <summary>
    /// An export split across two files, as large accounts are.
    /// </summary>
    /// <remarks>
    /// Only the first part carries personal_information; the second is a continuation of the same
    /// chat, which is what makes reading every <c>result*.json</c> in the folder necessary.
    /// </remarks>
    internal static string WriteMultiFile(string folder)
    {
        TelegramExportBuilder.Full()
            .Owner(OwnerId, "Owner", "Synthetic", username: "owner")
            .Chat("Sam Ruiz", "personal_chat", 100, c => c
                .Message(1, At(1554221523), Sam, "part one message"))
            .Write(folder, "result.json");

        TelegramExportBuilder.Full()
            .Chat("Sam Ruiz", "personal_chat", 100, c => c
                .Message(2, At(1554221600), Sam, "part two message"))
            .Write(folder, "result2.json");

        return folder;
    }

    /// <summary>A Hangouts Takeout export: one direct conversation and one group.</summary>
    internal static HangoutsExportBuilder Hangouts()
    {
        var owner = new HangoutsAccount("111111111111111111111", "Owner Synthetic");
        var sam = new HangoutsAccount("222222222222222222222", "Sam Ruiz");
        var marina = new HangoutsAccount("333333333333333333333", "Марина Коваль");

        return HangoutsExportBuilder.New()
            .Conversation("UgxABC123", "STICKY_ONE_TO_ONE", [owner, sam], c => c
                .Message("7-A-1", At(1451651696), sam, "the harbour was freezing")
                .Message("7-A-2", At(1451651796), owner,
                    HangoutsSegment.Text_("first line"),
                    HangoutsSegment.LineBreak,
                    HangoutsSegment.Text_("second line, see "),
                    HangoutsSegment.Link("example.org", "https://example.org"))
                .Hangout("7-A-3", At(1451651896), sam, "START_HANGOUT"),
                self: owner)
            .Conversation("UgxGROUP9", "GROUP", [owner, sam, marina], c => c
                .Message("7-B-1", At(1451738096), marina, "мы были в Праге весной"),
                name: "Prague trip");
    }

    internal static GoogleChatUser ChatOwner { get; } = new("Owner Synthetic", "owner@example.com");

    internal static GoogleChatUser ChatSam { get; } = new("Sam Ruiz", "Sam@Example.com");

    internal static GoogleChatUser ChatMarina { get; } = new("Марина Коваль", "marina@example.com");

    /// <summary>
    /// A Google Chat Takeout: a DM with an attachment present on disk, and a named space with a
    /// reaction.
    /// </summary>
    internal static GoogleChatExportBuilder GoogleChat(GoogleChatDateStyle style = GoogleChatDateStyle.MonthFirst) =>
        GoogleChatExportBuilder.New(ChatOwner, style)
            .Dm("abc123", [ChatOwner, ChatSam], g => g
                .Message("abc123/t1/m1", At(1615757463), ChatSam, "the harbour was freezing")
                .Message("abc123/t1/m2", At(1615757563), ChatOwner, "look", m => m
                    .Attachment("harbour.jpg", "File-harbour.jpg")
                    .Quotes("abc123/t1/m1"))
                .File("File-harbour.jpg", seed: 7))
            .Space("xyz789", "Prague trip", [ChatOwner, ChatSam, ChatMarina], g => g
                .Message("xyz789/t2/m1", At(1615843863), ChatMarina, "мы были в Праге весной", m => m
                    .Reaction("👍", "owner@example.com", "sam@example.com")
                    .Edited(At(1615843900))));

    private static DateTimeOffset Ms(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

    /// <summary>
    /// A Messenger download: a DM with a photo on disk and a call, and a named group whose text and
    /// names are Cyrillic and emoji — all of it double-encoded, as Meta writes it.
    /// </summary>
    internal static MetaExportBuilder Messenger(MetaLayout layout = MetaLayout.Current, string? owner = "Owner Synthetic") =>
        MetaExportBuilder.Facebook(owner, layout)
            .Thread("samruiz_1234567890", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .Message("Sam Ruiz", Ms(1615757463123), "the harbour was freezing")
                .Message("Owner Synthetic", Ms(1615757523123), "look", m => m.Photo("harbour.jpg", seed: 11))
                .Call("Sam Ruiz", Ms(1615757583123), 61))
            .Thread("praguetrip_9876543210", "Prague trip", ["Owner Synthetic", "Sam Ruiz", "Марина Коваль"], t => t
                .Message("Марина Коваль", Ms(1615843863000), "мы были в Праге весной 🌷", m => m
                    .Reaction("❤", "Sam Ruiz")));

    /// <summary>An Instagram download with one conversation.</summary>
    internal static MetaExportBuilder Instagram(MetaLayout layout = MetaLayout.Current) =>
        MetaExportBuilder.Instagram("Owner Synthetic", layout)
            .Thread("samruiz_555", "Sam Ruiz", ["Sam Ruiz", "Owner Synthetic"], t => t
                .Message("Sam Ruiz", Ms(1615757463123), "saw your story"));

    internal const string OwnNumber = "+15550000000";
    internal const string SamNumber = "+15551234567";
    internal const string AlexNumber = "+15559876543";

    /// <summary>
    /// An SMS backup: a conversation with Sam — one of their texts under a formatted number, one of
    /// yours with an emoji — a draft that must not be imported, and a group picture message you sent.
    /// </summary>
    internal static SmsBackupBuilder Sms(bool withPicture = true) =>
        SmsBackupBuilder.New()
            .Sms(SamNumber, At(1615757463), 1, "the harbour was freezing", "Sam Ruiz")
            .Sms(SamNumber, At(1615757523), 2, "we should go back 🌊", "Sam Ruiz")
            .Sms("+1 (555) 123-4567", At(1615843863), 1, "мы были в Праге весной", "Sam Ruiz")
            .Sms(SamNumber, At(1615843900), 3, "never sent", "Sam Ruiz")
            .Mms(At(1615930263), 2, [(OwnNumber, 137), (SamNumber, 151), (AlexNumber, 151)], "look",
                new SmsMmsPart("image/jpeg", "IMG_0001.jpg", withPicture ? SyntheticMedia.Bytes(seed: 21) : null),
                mId: "mms-0001", contactName: "Sam Ruiz, Alex Novak");

    /// <summary>A VK archive: one conversation with a person, one with a multi-person chat.</summary>
    internal static VkExportBuilder Vk() =>
        VkExportBuilder.New()
            .Conversation("222", "Sam Ruiz", c => c
                .Message(1001, At(1577882096), VkAuthor.Person(222, "Sam Ruiz"), "the harbour was freezing")
                .Message(1002, At(1577882110), VkAuthor.You, "we should go back")
                .Message(1003, At(1620032700), VkAuthor.Person(222, "Sam Ruiz"), "мы были в Праге весной",
                    attachment: ("Фотография", "https://vk.com/photo222_555"), withSeconds: false))
            .Conversation("-3001", "Поездка в Прагу", c => c
                .Message(2001, At(1580637600), VkAuthor.Person(333, "Марина Коваль"), "поезд в 07:40",
                    editedAt: At(1580641200)));

    /// <summary>A QIP profile with one contact's history in it.</summary>
    internal static string WriteQip(string folder, string ownUin = "12345678", string contactUin = "87654321")
    {
        var history = QipHistoryBuilder.HistoryFolder(folder, ownUin);
        var at = new DateTimeOffset(2008, 5, 1, 12, 0, 0, TimeSpan.Zero);

        QipHistoryBuilder.Write(
            history, contactUin, "Марина",
            new QipMessage(1, "привет", Outgoing: false, at),
            new QipMessage(2, "как дела", Outgoing: true, at.AddMinutes(1)),
            new QipMessage(3, "всё в порядке", Outgoing: false, at.AddMinutes(2)));

        return history;
    }

    /// <summary>An export's JSON as a stream, for the readers that take one.</summary>
    internal static Stream Stream(TelegramExportBuilder export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return new MemoryStream(Encoding.UTF8.GetBytes(export.Json()));
    }
}
