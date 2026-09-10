using System.Text.Json;
using Archive.Import.Synthetic;
using Archive.Import.Telegram;

namespace Archive.Import.Tests;

/// <summary>
/// One test per trap in §2, each against an export shape built to reproduce it (Exports).
/// </summary>
public sealed class TelegramNormalizerTests
{
    /// <summary>
    /// §2's first trap. The built `text` field says "WRONG" precisely so that a regression
    /// reading it produces an obviously wrong value rather than a plausible one.
    /// </summary>
    [Fact]
    public void Plaintext_comes_from_entities_and_never_from_the_text_field()
    {
        var messages = Normalize(Exports.TextStringVsEntities());

        Assert.Equal("see https://example.org for the map", messages[0].Plaintext);
        Assert.DoesNotContain("WRONG", messages[0].Plaintext, StringComparison.Ordinal);
    }

    [Fact]
    public void The_array_form_of_text_is_also_ignored()
    {
        var messages = Normalize(Exports.TextStringVsEntities());

        Assert.Equal("mixed array form", messages[1].Plaintext);
    }

    [Fact]
    public void Entities_are_preserved_for_rendering()
    {
        var messages = Normalize(Exports.TextStringVsEntities());

        Assert.NotNull(messages[0].EntitiesJson);
        Assert.Contains("\"link\"", messages[0].EntitiesJson!, StringComparison.Ordinal);
        Assert.Contains("\"bold\"", messages[1].EntitiesJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// §2: routing service messages through the from/from_id path is what produces phantom
    /// people named after actions.
    /// </summary>
    [Fact]
    public void Service_messages_are_attributed_to_the_actor_and_keep_their_action()
    {
        var messages = Normalize(Exports.ServiceMessages());

        Assert.All(messages, m => Assert.Equal("service", m.Kind));
        Assert.Equal(["create_group", "invite_members", "phone_call"], messages.Select(m => m.ServiceAction));
        Assert.All(messages, m => Assert.NotNull(m.Sender));
        Assert.Equal(["777001", "777001", "5001"], messages.Select(m => m.Sender!.SourceIdentityId));
    }

    [Fact]
    public void No_identity_is_ever_named_after_a_service_action()
    {
        var messages = Normalize(Exports.ServiceMessages());
        var names = messages.Select(m => m.Sender!.DisplayName).Distinct();

        Assert.DoesNotContain("phone_call", names);
        Assert.Equal(["Owner Synthetic", "Sam Ruiz"], names.Order());
    }

    [Fact]
    public void Identity_prefixes_are_stripped()
    {
        var messages = Normalize(Exports.PrefixedIds());

        Assert.Equal(["5001", "900", "5003"], messages.Select(m => m.Sender!.SourceIdentityId));
        Assert.All(messages, m => Assert.False(m.Sender!.IsSynthetic));
    }

    /// <summary>
    /// A prefix nobody has seen means a new class of participant. Guessing would create a
    /// parallel population of identities that never merges with the real one.
    /// </summary>
    [Fact]
    public void An_unknown_identity_prefix_is_fatal()
    {
        var exception = Assert.Throws<UnknownIdentityPrefixException>(() => Normalize(Exports.UnknownPrefix()));

        Assert.Equal("spaceship42", exception.Value);
    }

    [Fact]
    public void A_sender_with_no_id_becomes_a_synthetic_identity()
    {
        var message = Assert.Single(Normalize(Exports.NameOnlySender()));

        Assert.True(message.Sender!.IsSynthetic);
        Assert.Null(message.Sender.SourceIdentityId);
        Assert.Equal("Deleted Account", message.Sender.DisplayName);
    }

    [Fact]
    public void Saved_messages_are_a_conversation_with_yourself()
    {
        var chat = ReadChats(Exports.SavedMessages()).Single();

        Assert.Equal("saved", chat.ThreadKind);
    }

    [Fact]
    public void Timestamps_use_unixtime_and_record_the_senders_offset()
    {
        var message = Normalize(Exports.GroupAndDm())[0];

        Assert.Equal(1554221523, message.SentAtUnix);
        Assert.StartsWith("2019-04-02T16:12:03", message.SentAtUtc, StringComparison.Ordinal);

        // The export's local time is 18:12:03 against 16:12:03 UTC — two hours ahead, which is
        // Central European summer time. That offset is behavioural signal (§7) and is kept.
        Assert.Equal(120, message.TzOffsetMinutes);
    }

    [Fact]
    public void Replies_are_recorded_as_a_uid_in_the_same_thread()
    {
        var messages = Normalize(Exports.GroupAndDm());

        Assert.Equal("tg/100/1", messages[1].ReplyToUid);
        Assert.Null(messages[0].ReplyToUid);
    }

    [Fact]
    public void An_edit_is_recorded()
    {
        var message = Assert.Single(Normalize(Exports.ReactionsAndEdits()));

        Assert.NotNull(message.EditedAtUtc);
        Assert.StartsWith("2019-03-04T", message.EditedAtUtc!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Telegram names only recent reactors, so the named rows plus one anonymous remainder must
    /// still add up to the count it reported.
    /// </summary>
    [Fact]
    public void Reaction_counts_sum_to_the_reported_total()
    {
        var message = Assert.Single(Normalize(Exports.ReactionsAndEdits()));

        var thumbs = message.Reactions.Where(r => r.Emoji == "👍").ToArray();

        Assert.Equal(5, thumbs.Sum(r => r.Count));
        Assert.Equal(2, thumbs.Count(r => r.ActorIdentity is not null));
        Assert.Equal(3, Assert.Single(thumbs, r => r.ActorIdentity is null).Count);
    }

    [Fact]
    public void A_custom_emoji_reaction_keeps_its_document_id()
    {
        var message = Assert.Single(Normalize(Exports.ReactionsAndEdits()));

        var custom = Assert.Single(message.Reactions, r => r.CustomEmojiId is not null);

        Assert.Equal("5379748062124056162", custom.CustomEmojiId);
    }

    [Fact]
    public void Forwarded_messages_keep_their_origin()
    {
        var messages = Normalize(Exports.Forwards());

        Assert.Equal("Alex Novak", messages[0].ForwardedFrom);
        Assert.Null(messages[1].ForwardedFrom);
        Assert.Equal("@gif", messages[3].ViaBot);
    }

    /// <summary>
    /// The same sticker sent twice must normalize identically, which is what lets the media store
    /// write one file for both. Exports repeat stickers hundreds of times (§1).
    /// </summary>
    [Fact]
    public void The_same_sticker_in_two_messages_points_at_one_export_path()
    {
        var messages = Normalize(Exports.Forwards());

        var first = Assert.Single(messages[1].Media);
        var second = Assert.Single(messages[2].Media);

        Assert.Equal(first.ExportPath, second.ExportPath);
        Assert.Equal("sticker", first.MediaKind);
        Assert.Equal("🐢", first.StickerEmoji);
    }

    [Fact]
    public void A_video_carries_its_thumbnail_as_a_second_attachment()
    {
        var media = Normalize(Exports.Forwards())[3].Media;

        Assert.Equal(2, media.Count);
        Assert.Equal("animation", media[0].MediaKind);
        Assert.Equal("thumbnail", media[1].MediaKind);
        Assert.Equal(4, media[0].DurationSeconds);
    }

    /// <summary>
    /// §2: a file the export omitted is recorded, not dropped. The message still happened, and
    /// knowing a photo was there beats a silent gap.
    /// </summary>
    [Fact]
    public void Media_the_export_omitted_is_recorded_with_a_reason()
    {
        var messages = Normalize(Exports.MissingMedia());

        var photo = Assert.Single(messages[0].Media);
        Assert.NotNull(photo.MissingReason);
        Assert.Equal("photo", photo.MediaKind);
        Assert.Equal(1280, photo.Width);

        var voice = Assert.Single(messages[1].Media);
        Assert.NotNull(voice.MissingReason);
        Assert.Equal("voice", voice.MediaKind);
        Assert.Equal(12, voice.DurationSeconds);
    }

    [Fact]
    public void The_content_hash_changes_with_the_text_and_not_with_anything_else()
    {
        var messages = Normalize(Exports.TextStringVsEntities());

        Assert.NotEqual(messages[0].ContentHash, messages[1].ContentHash);

        // Re-normalizing the identical input must produce the identical hash, or re-import can
        // never distinguish "already have this" from "this was edited".
        Assert.Equal(messages[0].ContentHash, Normalize(Exports.TextStringVsEntities())[0].ContentHash);
    }

    [Fact]
    public void Uids_are_stable_and_scoped_to_the_thread()
    {
        var messages = Normalize(Exports.GroupAndDm());

        Assert.Equal("tg/100/1", messages[0].Uid);
        Assert.Equal("tg/200/11", messages[2].Uid);
    }

    private static List<NormalizedMessage> Normalize(TelegramExportBuilder export)
    {
        var sink = new NormalizingSink();
        using var stream = Exports.Stream(export);
        TelegramExportReader.Read(stream, sink);
        return sink.Messages;
    }

    private static List<TelegramChatHeader> ReadChats(TelegramExportBuilder export)
    {
        var sink = new NormalizingSink();
        using var stream = Exports.Stream(export);
        TelegramExportReader.Read(stream, sink);
        return sink.Chats;
    }

    private sealed class NormalizingSink : ITelegramExportSink
    {
        internal List<TelegramChatHeader> Chats { get; } = [];

        internal List<NormalizedMessage> Messages { get; } = [];

        public void OnPersonalInformation(JsonElement element) { }

        public void OnChat(TelegramChatHeader chat) => Chats.Add(chat);

        public void OnMessage(TelegramChatHeader chat, JsonElement message) =>
            Messages.Add(TelegramNormalizer.Normalize(chat, message));
    }
}
