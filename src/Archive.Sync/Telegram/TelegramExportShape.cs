using System.Globalization;
using System.Text;
using TL;

// TL has types of its own called JsonObject and JsonArray — Telegram's own JSON value types, which
// have nothing to do with writing JSON here. Aliased rather than qualified at every use.
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace Archive.Sync.Telegram;

/// <summary>An attachment on a translated message, with what is needed to fetch its bytes.</summary>
/// <param name="ExportPath">
/// What the export would call the file. There is no file, so this is a stable name for the same
/// object instead — it is part of the message's content hash, so it must not change between runs.
/// </param>
internal sealed record TelegramAttachment(
    string ExportPath,
    long Size,
    PhotoBase? Photo,
    DocumentBase? Document,
    string? FileName,
    bool IsPhoto,
    bool IsVoice,
    bool IsVideo);

/// <summary>A message in the shape the export writes it, plus its attachments.</summary>
internal sealed record TranslatedMessage(JsonObject Json, IReadOnlyList<TelegramAttachment> Attachments);

/// <summary>
/// Turns a message off the wire into the JSON Telegram Desktop's export writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so there is one normalizer, not two.</b> The archive's Telegram reader already
/// knows every trap in §2 — the polymorphic <c>text</c> field, prefixed <c>from_id</c>, service
/// messages carrying <c>actor</c> rather than <c>from</c>. Writing a second reader for the API
/// would mean two implementations of all of it, which is two chances to disagree; and if they
/// disagree, the same message arriving by both routes is stored twice or looks edited. So the
/// connection produces the export's own shape and <c>TelegramNormalizer</c> reads it, exactly as
/// it reads a file.
/// </para>
/// <para>
/// Two things here can only be confirmed against a real export, and <c>ahistory sync-check</c>
/// exists to confirm them: that a supergroup's chat id is written the same way on both sides, and
/// that entities convert to the same array. D22 is the standing warning — a translator and a
/// reader written by the same hand from the same reading of a format will agree with each other
/// and can still both be wrong.
/// </para>
/// </remarks>
internal static class TelegramExportShape
{
    /// <summary>Names a photo or document stably, so its message's content hash does not move.</summary>
    internal static string PathFor(string kind, long id) =>
        string.Create(CultureInfo.InvariantCulture, $"telegram:{kind}/{id}");

    /// <summary>
    /// Translates one message. Returns null for what the export does not write at all.
    /// </summary>
    /// <param name="selfUserId">
    /// The signed-in account. An outgoing message in a private chat carries no sender — the sender
    /// is the account itself — and without this half of every conversation lands on the wrong side.
    /// </param>
    /// <param name="nameOf">A peer's display name, for the fields the export writes names into.</param>
    public static TranslatedMessage? Translate(MessageBase message, long selfUserId, Func<Peer, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(nameOf);

        return message switch
        {
            Message m => Ordinary(m, selfUserId, nameOf),
            MessageService s => Service(s, selfUserId, nameOf),

            // MessageEmpty is a hole the server reports where a message used to be. There is
            // nothing to store and nothing to say about it.
            _ => null,
        };
    }

    private static TranslatedMessage Ordinary(Message message, long selfUserId, Func<Peer, string?> nameOf)
    {
        var json = new JsonObject
        {
            ["id"] = message.id,
            ["type"] = "message",
        };

        WriteDate(json, message.date);

        var from = message.from_id ?? message.peer_id;

        if (message.flags.HasFlag(Message.Flags.out_))
        {
            // An outgoing message in a private chat carries no from_id: the sender is the account
            // itself, and peer_id is the other person. Read straight through, half of every
            // conversation would land on the wrong side.
            from = message.from_id ?? new PeerUser { user_id = selfUserId };
        }

        if (from is not null)
        {
            json["from"] = nameOf(from);
            json["from_id"] = PeerId(from);
        }

        if (message.fwd_from is { } forwarded)
        {
            json["forwarded_from"] = forwarded.from_name
                ?? (forwarded.from_id is { } origin ? nameOf(origin) : null);
        }

        if (message.reply_to is MessageReplyHeader { reply_to_peer_id: null } reply && reply.reply_to_msg_id != 0)
        {
            // Only a reply within the same chat. A reply to another chat's message has no uid here
            // to point at, and inventing one would link a message to whatever happened to share
            // that number.
            json["reply_to_message_id"] = reply.reply_to_msg_id;
        }

        if (message.flags.HasFlag(Message.Flags.has_edit_date))
        {
            json["edited"] = Local(message.edit_date);
            json["edited_unixtime"] = Unix(message.edit_date);
        }

        var attachments = WriteMedia(json, message.media);

        WriteText(json, message.message, message.entities);
        WriteReactions(json, message.reactions, nameOf);

        return new TranslatedMessage(json, attachments);
    }

    /// <summary>
    /// §2: a service message carries <c>actor</c>, not <c>from</c> — reading them through the
    /// ordinary path is what produces phantom people named "phone call".
    /// </summary>
    private static TranslatedMessage Service(MessageService message, long selfUserId, Func<Peer, string?> nameOf)
    {
        var json = new JsonObject
        {
            ["id"] = message.id,
            ["type"] = "service",
            ["action"] = ActionName(message.action),
        };

        WriteDate(json, message.date);

        var actor = message.from_id
            ?? (message.flags.HasFlag(MessageService.Flags.out_) ? new PeerUser { user_id = selfUserId } : message.peer_id);

        if (actor is not null)
        {
            json["actor"] = nameOf(actor);
            json["actor_id"] = PeerId(actor);
        }

        json["text"] = string.Empty;
        json["text_entities"] = new JsonArray();

        return new TranslatedMessage(json, []);
    }

    /// <summary>
    /// The export's name for a service action.
    /// </summary>
    /// <remarks>
    /// Only the actions whose export spelling is known are named; anything else becomes the TL
    /// type's own name in snake case. That is deliberately not a guess at what the export would
    /// have written — it is a different, honest name, and <c>sync-check</c> lists the ones that
    /// differ so they can be filled in from a real export rather than from memory (D20, D22).
    /// </remarks>
    internal static string ActionName(MessageAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return action switch
        {
            MessageActionChatCreate => "create_group",
            MessageActionChannelCreate => "create_channel",
            MessageActionChatEditTitle => "edit_group_title",
            MessageActionChatEditPhoto => "edit_group_photo",
            MessageActionChatDeletePhoto => "delete_group_photo",
            MessageActionChatAddUser => "invite_members",
            MessageActionChatDeleteUser => "remove_members",
            MessageActionChatJoinedByLink => "join_group_by_link",
            MessageActionChatMigrateTo => "migrate_to_supergroup",
            MessageActionChannelMigrateFrom => "migrate_from_group",
            MessageActionPinMessage => "pin_message",
            MessageActionHistoryClear => "clear_history",
            MessageActionGameScore => "score_in_game",
            MessageActionPhoneCall => "phone_call",
            MessageActionScreenshotTaken => "take_screenshot",
            _ => SnakeCase(action.GetType().Name["MessageAction".Length..]),
        };
    }

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes <c>text_entities</c>, the only text field the reader reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The export writes the message as consecutive runs: plain stretches and entity stretches,
    /// in order, each carrying its own text. Concatenating their <c>text</c> values reproduces the
    /// message exactly, which is what the reader relies on.
    /// </para>
    /// <para>
    /// Telegram's offsets are UTF-16 code units, which is what a .NET string is indexed in, so no
    /// conversion is needed — and getting that wrong would cut emoji in half rather than fail.
    /// </para>
    /// <para>
    /// An entity starting inside another is folded into the one that contains it: the export's
    /// format has no nesting, so bold-inside-a-link is written as the link. The text still comes
    /// out whole, which is what search and the content hash rest on.
    /// </para>
    /// </remarks>
    internal static void WriteText(JsonObject json, string? text, MessageEntity[]? entities)
    {
        text ??= string.Empty;
        json["text"] = text;

        var array = new JsonArray();

        if (text.Length == 0)
        {
            json["text_entities"] = array;
            return;
        }

        var ordered = (entities ?? [])
            .Where(e => e.Offset >= 0 && e.Length > 0 && e.Offset + e.Length <= text.Length)
            .OrderBy(e => e.Offset)
            .ThenByDescending(e => e.Length)
            .ToArray();

        var at = 0;

        foreach (var entity in ordered)
        {
            if (entity.Offset < at)
            {
                continue;
            }

            if (entity.Offset > at)
            {
                array.Add(Run("plain", text[at..entity.Offset]));
            }

            array.Add(Entity(entity, text.Substring(entity.Offset, entity.Length)));
            at = entity.Offset + entity.Length;
        }

        if (at < text.Length)
        {
            array.Add(Run("plain", text[at..]));
        }

        json["text_entities"] = array;
    }

    private static JsonObject Run(string type, string text) =>
        new() { ["type"] = type, ["text"] = text };

    private static JsonObject Entity(MessageEntity entity, string text)
    {
        var run = Run(TypeName(entity), text);

        switch (entity)
        {
            case MessageEntityTextUrl url:
                run["href"] = url.url;
                break;
            case MessageEntityPre pre:
                run["language"] = pre.language;
                break;
            case MessageEntityMentionName mention:
                run["user_id"] = mention.user_id;
                break;
            case MessageEntityCustomEmoji emoji:
                run["document_id"] = emoji.document_id.ToString(CultureInfo.InvariantCulture);
                break;
        }

        return run;
    }

    private static string TypeName(MessageEntity entity) => entity switch
    {
        MessageEntityMention => "mention",
        MessageEntityHashtag => "hashtag",
        MessageEntityBotCommand => "bot_command",
        MessageEntityUrl => "link",
        MessageEntityEmail => "email",
        MessageEntityBold => "bold",
        MessageEntityItalic => "italic",
        MessageEntityCode => "code",
        MessageEntityPre => "pre",
        MessageEntityTextUrl => "text_link",
        MessageEntityMentionName => "mention_name",
        MessageEntityPhone => "phone",
        MessageEntityCashtag => "cashtag",
        MessageEntityUnderline => "underline",
        MessageEntityStrike => "strikethrough",
        MessageEntityBankCard => "bank_card",
        MessageEntitySpoiler => "spoiler",
        MessageEntityCustomEmoji => "custom_emoji",
        MessageEntityBlockquote => "blockquote",
        _ => "unknown",
    };

    /// <summary>
    /// Writes the media fields the reader looks for, and returns what would have to be downloaded.
    /// </summary>
    /// <remarks>
    /// Only photos and documents. The export writes other media — a location, a poll, a contact —
    /// as fields of their own, and the reader does not treat any of them as an attachment, so
    /// inventing entries for them here would put files in the archive that never existed.
    /// </remarks>
    private static IReadOnlyList<TelegramAttachment> WriteMedia(JsonObject json, MessageMedia? media)
    {
        switch (media)
        {
            case MessageMediaPhoto { photo: Photo photo }:
            {
                var path = PathFor("photo", photo.id);
                var largest = photo.LargestPhotoSize;

                json["photo"] = path;

                if (largest is PhotoSize size)
                {
                    json["width"] = size.w;
                    json["height"] = size.h;
                }

                return [new TelegramAttachment(
                    path, largest is PhotoSize s ? s.size : 0, photo, null, null,
                    IsPhoto: true, IsVoice: false, IsVideo: false)];
            }

            case MessageMediaDocument { document: Document document }:
            {
                var path = PathFor("document", document.id);

                json["file"] = path;
                json["mime_type"] = document.mime_type;

                var fileName = document.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name;

                if (fileName is not null)
                {
                    json["file_name"] = fileName;
                }

                var audio = document.attributes.OfType<DocumentAttributeAudio>().FirstOrDefault();
                var video = document.attributes.OfType<DocumentAttributeVideo>().FirstOrDefault();
                var sticker = document.attributes.OfType<DocumentAttributeSticker>().FirstOrDefault();
                var animated = document.attributes.OfType<DocumentAttributeAnimated>().FirstOrDefault();

                var isVoice = audio?.flags.HasFlag(DocumentAttributeAudio.Flags.voice) == true;
                var isRound = video?.flags.HasFlag(DocumentAttributeVideo.Flags.round_message) == true;

                var kind = (sticker, animated, isVoice, isRound, audio, video) switch
                {
                    (not null, _, _, _, _, _) => "sticker",
                    (_, not null, _, _, _, _) => "animation",
                    (_, _, true, _, _, _) => "voice_message",
                    (_, _, _, true, _, _) => "video_message",
                    (_, _, _, _, not null, _) => "audio_file",
                    (_, _, _, _, _, not null) => "video_file",
                    _ => null,
                };

                if (kind is not null)
                {
                    json["media_type"] = kind;
                }

                if (sticker?.alt is { Length: > 0 } alt)
                {
                    json["sticker_emoji"] = alt;
                }

                if (video is not null)
                {
                    json["width"] = video.w;
                    json["height"] = video.h;
                    json["duration_seconds"] = (long)video.duration;
                }
                else if (audio is not null)
                {
                    json["duration_seconds"] = audio.duration;
                }

                return [new TelegramAttachment(
                    path, document.size, null, document, fileName,
                    IsPhoto: false, IsVoice: isVoice, IsVideo: video is not null)];
            }

            default:
                return [];
        }
    }

    private static void WriteReactions(JsonObject json, MessageReactions? reactions, Func<Peer, string?> nameOf)
    {
        if (reactions?.results is not { Length: > 0 } results)
        {
            return;
        }

        var array = new JsonArray();

        foreach (var result in results)
        {
            var entry = new JsonObject { ["count"] = result.count };

            switch (result.reaction)
            {
                case ReactionEmoji emoji:
                    entry["type"] = "emoji";
                    entry["emoji"] = emoji.emoticon;
                    break;
                case ReactionCustomEmoji custom:
                    entry["type"] = "custom_emoji";
                    entry["document_id"] = custom.document_id.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    entry["type"] = "unknown";
                    break;
            }

            // Telegram reports a total plus a few recent reactors. Naming those and leaving the
            // remainder anonymous is what keeps the stored counts summing to the real total.
            var recent = new JsonArray();

            foreach (var reactor in reactions.recent_reactions ?? [])
            {
                if (!SameReaction(reactor.reaction, result.reaction) || reactor.peer_id is not { } peer)
                {
                    continue;
                }

                recent.Add(new JsonObject
                {
                    ["from"] = nameOf(peer),
                    ["from_id"] = PeerId(peer),
                    ["date"] = Local(reactor.date),
                    ["date_unixtime"] = Unix(reactor.date),
                });
            }

            if (recent.Count > 0)
            {
                entry["recent"] = recent;
            }

            array.Add(entry);
        }

        json["reactions"] = array;
    }

    private static bool SameReaction(Reaction left, Reaction right) => (left, right) switch
    {
        (ReactionEmoji a, ReactionEmoji b) => string.Equals(a.emoticon, b.emoticon, StringComparison.Ordinal),
        (ReactionCustomEmoji a, ReactionCustomEmoji b) => a.document_id == b.document_id,
        _ => false,
    };

    /// <summary>
    /// The two timestamps the export writes: the unix one, which is authoritative, and the local
    /// wall clock, from which the reader works out the offset the sender saw (D5).
    /// </summary>
    private static void WriteDate(JsonObject json, DateTime date)
    {
        json["date"] = Local(date);
        json["date_unixtime"] = Unix(date);
    }

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static string Unix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>§2: identity references are prefixed — <c>user123</c>, <c>channel456</c>.</summary>
    internal static string PeerId(Peer peer) => peer switch
    {
        PeerUser user => "user" + user.user_id.ToString(CultureInfo.InvariantCulture),
        PeerChat chat => "chat" + chat.chat_id.ToString(CultureInfo.InvariantCulture),
        PeerChannel channel => "channel" + channel.channel_id.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException(
            $"Unrecognized Telegram peer '{peer.GetType().Name}'. Teach the translator about it rather than guessing."),
    };
}
