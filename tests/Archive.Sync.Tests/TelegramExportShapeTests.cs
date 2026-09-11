using System.Text.Json;
using Archive.Import.Telegram;
using Archive.Sync.Telegram;
using TL;

namespace Archive.Sync.Tests;

/// <summary>
/// The translation a connected account goes through: a message off the wire, written in the shape
/// the export writes it, read by the archive's own Telegram reader.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here goes through <c>TelegramNormalizer</c> rather than reading the JSON
/// directly, because the JSON is a means and the normalized message is the end. What matters is
/// that the uid, the text, the sender and the content hash come out the way the same message would
/// from an export — otherwise the two routes store it twice.
/// </para>
/// <para>
/// D22 applies in full: these tests prove the translator does what was intended, not that what was
/// intended matches what Telegram Desktop writes. Only a real export can say that, which is what
/// <c>ahistory sync-check</c> is for.
/// </para>
/// </remarks>
public sealed class TelegramExportShapeTests
{
    private const long Me = 777001;
    private static readonly DateTime Sent = new(2019, 4, 2, 17, 12, 3, DateTimeKind.Utc);

    private static string NameOf(Peer peer) => peer switch
    {
        PeerUser { user_id: Me } => "Owner Synthetic",
        PeerUser => "Sam Ruiz",
        _ => "Prague trip",
    };

    /// <summary>Runs a message through the translator and then through the archive's reader.</summary>
    private static Archive.Import.NormalizedMessage Normalize(
        MessageBase message, string chatId = "5001", string type = "personal_chat")
    {
        var translated = TelegramExportShape.Translate(message, Me, NameOf);

        Assert.NotNull(translated);

        using var document = JsonDocument.Parse(translated!.Json.ToJsonString());

        return TelegramNormalizer.Normalize(
            new TelegramChatHeader("Sam Ruiz", type, chatId, IsLeft: false), document.RootElement);
    }

    private static Message Incoming(string text, MessageEntity[]? entities = null, MessageMedia? media = null) =>
        new()
        {
            id = 42,
            flags = Message.Flags.has_from_id | (entities is null ? 0 : Message.Flags.has_entities)
                    | (media is null ? 0 : Message.Flags.has_media),
            from_id = new PeerUser { user_id = 5001 },
            peer_id = new PeerUser { user_id = 5001 },
            date = Sent,
            message = text,
            entities = entities,
            media = media,
        };

    [Fact]
    public void A_message_from_the_account_reads_as_the_same_message_an_export_would_give()
    {
        var normalized = Normalize(Incoming("the harbour was freezing"));

        Assert.Equal("tg/5001/42", normalized.Uid);
        Assert.Equal("the harbour was freezing", normalized.Plaintext);
        Assert.Equal("message", normalized.Kind);
        Assert.Equal("5001", normalized.Sender!.SourceIdentityId);
        Assert.Equal(Sent, DateTimeOffset.FromUnixTimeSeconds(normalized.SentAtUnix).UtcDateTime);
    }

    /// <summary>
    /// An outgoing private message carries no sender: the sender is the account itself. Read
    /// straight through, half of every conversation lands on the wrong side.
    /// </summary>
    [Fact]
    public void My_own_message_is_attributed_to_me()
    {
        var mine = new Message
        {
            id = 43,
            flags = Message.Flags.out_,
            peer_id = new PeerUser { user_id = 5001 },
            date = Sent,
            message = "we should go back",
        };

        var normalized = Normalize(mine);

        Assert.Equal("777001", normalized.Sender!.SourceIdentityId);
        Assert.False(normalized.Sender.IsSynthetic);
    }

    [Fact]
    public void Formatting_survives_as_the_entities_the_reader_keeps()
    {
        var normalized = Normalize(Incoming(
            "see here",
            [new MessageEntityTextUrl { offset = 4, length = 4, url = "https://example.org/" }]));

        Assert.Equal("see here", normalized.Plaintext);
        Assert.Contains("text_link", normalized.EntitiesJson!, StringComparison.Ordinal);
        Assert.Contains("https://example.org/", normalized.EntitiesJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The export's format has no nesting, so bold inside a link is written as the link. What must
    /// not happen is losing or duplicating the text, which is what search and the hash rest on.
    /// </summary>
    [Fact]
    public void An_entity_inside_another_does_not_cost_any_text()
    {
        var normalized = Normalize(Incoming(
            "see here now",
            [
                new MessageEntityTextUrl { offset = 4, length = 4, url = "https://example.org/" },
                new MessageEntityBold { offset = 5, length = 2 },
            ]));

        Assert.Equal("see here now", normalized.Plaintext);
    }

    /// <summary>Emoji are two UTF-16 units, which is what Telegram counts offsets in.</summary>
    [Fact]
    public void An_offset_past_an_emoji_lands_where_it_should()
    {
        const string Text = "👍 bold";

        var normalized = Normalize(Incoming(Text, [new MessageEntityBold { offset = 3, length = 4 }]));

        Assert.Equal(Text, normalized.Plaintext);
        Assert.Contains("\"bold\"", normalized.EntitiesJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_voice_message_arrives_as_a_voice_attachment()
    {
        var document = new Document
        {
            id = 9001,
            mime_type = "audio/ogg",
            size = 4096,
            attributes =
            [
                new DocumentAttributeAudio { flags = DocumentAttributeAudio.Flags.voice, duration = 7 },
            ],
        };

        var normalized = Normalize(Incoming(string.Empty, media: new MessageMediaDocument { document = document }));

        var media = Assert.Single(normalized.Media);

        Assert.Equal("voice", media.MediaKind);
        Assert.Equal("telegram:document/9001", media.ExportPath);
        Assert.Equal(7, media.DurationSeconds);
        Assert.Null(media.MissingReason);
    }

    /// <summary>
    /// The attachment's name is part of the message's content hash, so it has to be the same on
    /// every run — an id, not anything that moves.
    /// </summary>
    [Fact]
    public void The_same_message_hashes_the_same_way_twice()
    {
        var media = new MessageMediaPhoto
        {
            photo = new Photo { id = 5566, sizes = [new PhotoSize { type = "x", w = 800, h = 600, size = 1024 }] },
        };

        var first = Normalize(Incoming("look", media: media));
        var second = Normalize(Incoming("look", media: media));

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal("telegram:photo/5566", Assert.Single(first.Media).ExportPath);
    }

    /// <summary>
    /// §2: a service message carries an actor rather than a sender, and reading it through the
    /// ordinary path is what produces phantom people named "phone call".
    /// </summary>
    [Fact]
    public void A_service_message_is_an_event_with_an_actor()
    {
        var service = new MessageService
        {
            id = 44,
            flags = MessageService.Flags.has_from_id,
            from_id = new PeerUser { user_id = 5001 },
            peer_id = new PeerUser { user_id = 5001 },
            date = Sent,
            action = new MessageActionPhoneCall { duration = 61 },
        };

        var normalized = Normalize(service);

        Assert.Equal("service", normalized.Kind);
        Assert.Equal("phone_call", normalized.ServiceAction);
        Assert.Equal("5001", normalized.Sender!.SourceIdentityId);
        Assert.Equal(string.Empty, normalized.Plaintext);
    }

    /// <summary>
    /// An action whose export spelling nobody has confirmed gets a name of its own rather than a
    /// guess at Telegram's (D20). It is still an event, and still attributed.
    /// </summary>
    [Fact]
    public void An_unfamiliar_action_is_named_rather_than_guessed_at()
    {
        Assert.Equal("phone_call", TelegramExportShape.ActionName(new MessageActionPhoneCall()));
        Assert.Equal("invite_members", TelegramExportShape.ActionName(new MessageActionChatAddUser()));
        Assert.Equal("set_chat_theme", TelegramExportShape.ActionName(new MessageActionSetChatTheme()));
    }

    [Fact]
    public void A_reply_points_at_the_message_it_answers()
    {
        var reply = Incoming("yes");
        reply.flags |= Message.Flags.has_reply_to;
        reply.reply_to = new MessageReplyHeader { reply_to_msg_id = 40 };

        var normalized = Normalize(reply);

        Assert.Equal("tg/5001/40", normalized.ReplyToUid);
    }

    [Fact]
    public void Reactions_keep_both_who_reacted_and_how_many_did()
    {
        var message = Incoming("booked");
        message.flags |= Message.Flags.has_reactions;
        message.reactions = new MessageReactions
        {
            results = [new ReactionCount { reaction = new ReactionEmoji { emoticon = "👍" }, count = 3 }],
            recent_reactions =
            [
                new MessagePeerReaction
                {
                    peer_id = new PeerUser { user_id = 5001 },
                    reaction = new ReactionEmoji { emoticon = "👍" },
                    date = Sent,
                },
            ],
        };

        var normalized = Normalize(message);

        Assert.Equal(2, normalized.Reactions.Count);
        Assert.Equal(3, normalized.Reactions.Sum(r => r.Count));
        Assert.Contains(normalized.Reactions, r => r.ActorIdentity?.SourceIdentityId == "5001");
    }

    /// <summary>A group message keys its identities the way the export does, prefix and all.</summary>
    [Fact]
    public void A_group_message_keeps_its_sender_and_its_room_apart()
    {
        var message = new Message
        {
            id = 7,
            flags = Message.Flags.has_from_id,
            from_id = new PeerUser { user_id = 5003 },
            peer_id = new PeerChat { chat_id = 200 },
            date = Sent,
            message = "поезд в 07:40",
        };

        var normalized = Normalize(message, chatId: "200", type: "private_group");

        Assert.Equal("tg/200/7", normalized.Uid);
        Assert.Equal("5003", normalized.Sender!.SourceIdentityId);
        Assert.Equal("поезд в 07:40", normalized.Plaintext);
    }
}
