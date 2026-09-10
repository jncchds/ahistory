using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Archive.Import.Telegram;

/// <summary>
/// Turns one raw Telegram message into a <see cref="NormalizedMessage"/>.
/// </summary>
/// <remarks>
/// Every trap listed in §2 is handled here explicitly, and each has an export shape built for it
/// in <c>Archive.Import.Tests.Exports</c>. The single most important one: the polymorphic
/// <c>text</c> field is never read.
/// </remarks>
public static class TelegramNormalizer
{
    public const string Platform = "telegram";

    /// <summary>The sentinel Telegram writes when the export excluded the file itself.</summary>
    private const string NotIncludedMarker = "(File not included";

    public static NormalizedMessage Normalize(TelegramChatHeader chat, JsonElement message)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var threadId = chat.SourceThreadId;
        var messageId = ReadId(message);
        var isService = String(message, "type") == "service";

        var (plaintext, entitiesJson) = ReadText(message);
        var (sentAtUtc, sentAtUnix, tzOffset) = ReadTimestamp(message);

        var sender = isService
            ? ReadIdentity(message, "actor", "actor_id")
            : ReadIdentity(message, "from", "from_id");

        var media = ReadMedia(message);
        var serviceAction = isService ? String(message, "action") : null;

        return new NormalizedMessage
        {
            Uid = Uid(threadId, messageId),
            SourceThreadId = threadId,
            Kind = isService ? "service" : "message",
            Sender = sender,
            ServiceAction = serviceAction,
            SentAtUtc = sentAtUtc,
            SentAtUnix = sentAtUnix,
            TzOffsetMinutes = tzOffset,
            Plaintext = plaintext,
            EntitiesJson = entitiesJson,
            ContentHash = ContentHash(plaintext, entitiesJson, serviceAction, media),
            ReplyToUid = ReadReplyToUid(message, threadId),
            ForwardedFrom = String(message, "forwarded_from"),
            ForwardedAtUtc = ReadOptionalTimestamp(message, "date", "date_unixtime", onlyIfForwarded: true),
            ViaBot = String(message, "via_bot"),
            EditedAtUtc = ReadOptionalTimestamp(message, "edited", "edited_unixtime", onlyIfForwarded: false),
            RawJson = message.GetRawText(),
            Media = media,
            Reactions = ReadReactions(message),
        };
    }

    public static string Uid(string sourceThreadId, string messageId) =>
        $"tg/{sourceThreadId}/{messageId}";

    /// <summary>
    /// §2: <c>text</c> is either a string or an array mixing strings and objects. It is ignored
    /// entirely; <c>text_entities</c> is always the array form and is the only thing read.
    /// </summary>
    /// <remarks>
    /// Concatenating the entity texts reproduces the message exactly, and keeping the entity
    /// array preserves what the plain text loses — which parts were links, code or mentions.
    /// Rendering needs that; search does not.
    /// </remarks>
    private static (string Plaintext, string? EntitiesJson) ReadText(JsonElement message)
    {
        if (!message.TryGetProperty("text_entities", out var entities) ||
            entities.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, null);
        }

        var builder = new StringBuilder();

        foreach (var entity in entities.EnumerateArray())
        {
            if (entity.ValueKind == JsonValueKind.Object &&
                entity.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        var json = entities.GetArrayLength() > 0 ? entities.GetRawText() : null;

        return (builder.ToString(), json);
    }

    /// <summary>
    /// Reads the instant a message was sent, plus the offset the sender saw.
    /// </summary>
    /// <remarks>
    /// <c>date_unixtime</c> is authoritative and unambiguous; <c>date</c> is local wall-clock with
    /// no offset attached. Keeping both lets the archive sort correctly across platforms and time
    /// zones while still being able to say "it was 3am for them", which is behavioural signal (§7).
    /// </remarks>
    private static (string Utc, long Unix, long? TzOffsetMinutes) ReadTimestamp(JsonElement message)
    {
        var unixText = String(message, "date_unixtime");
        var localText = String(message, "date");

        if (unixText is not null && long.TryParse(unixText, CultureInfo.InvariantCulture, out var unix))
        {
            var utc = DateTimeOffset.FromUnixTimeSeconds(unix);
            long? offset = null;

            if (localText is not null &&
                DateTime.TryParse(localText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                offset = (long)Math.Round((local - utc.UtcDateTime).TotalMinutes);
            }

            return (utc.ToString("O"), unix, offset);
        }

        // Older exports carry only the local string. It is treated as UTC, because inventing an
        // offset would be worse than admitting there isn't one: the null says "unknown".
        if (localText is not null &&
            DateTime.TryParse(localText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallback))
        {
            var assumed = new DateTimeOffset(fallback, TimeSpan.Zero);
            return (assumed.ToString("O"), assumed.ToUnixTimeSeconds(), null);
        }

        throw new InvalidDataException("Message has neither date_unixtime nor a parsable date.");
    }

    private static string? ReadOptionalTimestamp(
        JsonElement message,
        string localProperty,
        string unixProperty,
        bool onlyIfForwarded)
    {
        if (onlyIfForwarded && String(message, "forwarded_from") is null)
        {
            return null;
        }

        var unixText = String(message, unixProperty);

        if (unixText is not null && long.TryParse(unixText, CultureInfo.InvariantCulture, out var unix))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix).ToString("O");
        }

        var localText = String(message, localProperty);

        if (!onlyIfForwarded && localText is not null &&
            DateTime.TryParse(localText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return new DateTimeOffset(local, TimeSpan.Zero).ToString("O");
        }

        return null;
    }

    /// <summary>
    /// §2: service messages carry <c>actor</c>/<c>actor_id</c>, not <c>from</c>/<c>from_id</c>.
    /// Reading them through the normal path is what produces phantom people named "phone call".
    /// </summary>
    private static NormalizedIdentity? ReadIdentity(JsonElement message, string nameProperty, string idProperty)
    {
        var name = String(message, nameProperty);
        var rawId = String(message, idProperty);

        if (rawId is not null)
        {
            var reference = IdentityRef.Parse(rawId);

            return new NormalizedIdentity(
                Platform,
                reference.Id,
                Handle: null,
                DisplayName: name ?? reference.Id,
                IsSynthetic: false);
        }

        if (name is not null)
        {
            // §2: no id, only a name. Keyed by name, and flagged so the merge UI can treat it as
            // the guess it is.
            return new NormalizedIdentity(Platform, null, null, name, IsSynthetic: true);
        }

        return null;
    }

    private static string? ReadReplyToUid(JsonElement message, string threadId)
    {
        if (!message.TryGetProperty("reply_to_message_id", out var reply))
        {
            return null;
        }

        var id = reply.ValueKind switch
        {
            JsonValueKind.Number when reply.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => reply.GetString(),
            _ => null,
        };

        return id is null ? null : Uid(threadId, id);
    }

    /// <summary>
    /// Collects attachments, including the ones the export left out.
    /// </summary>
    /// <remarks>
    /// A missing file is recorded rather than dropped: the message still happened, and knowing
    /// that a photo was there is more useful than a silent gap. The sentinel Telegram writes is
    /// matched by prefix because its wording has changed between versions.
    /// </remarks>
    private static List<NormalizedMedia> ReadMedia(JsonElement message)
    {
        var media = new List<NormalizedMedia>();

        var mime = String(message, "mime_type");
        var fileName = String(message, "file_name");
        var width = Number(message, "width");
        var height = Number(message, "height");
        var duration = Number(message, "duration_seconds");
        var stickerEmoji = String(message, "sticker_emoji");

        if (String(message, "photo") is { } photo)
        {
            media.Add(new NormalizedMedia(
                photo, "photo", fileName, mime, MissingReason(photo), null, width, height, null));
        }

        if (String(message, "file") is { } file)
        {
            media.Add(new NormalizedMedia(
                file, MediaKind(String(message, "media_type"), mime), fileName, mime,
                MissingReason(file), stickerEmoji, width, height, duration));
        }

        // Thumbnails are cheap and make a video list render without decoding anything.
        if (String(message, "thumbnail") is { } thumbnail)
        {
            media.Add(new NormalizedMedia(
                thumbnail, "thumbnail", null, null, MissingReason(thumbnail), null, null, null, null));
        }

        return media;
    }

    private static string? MissingReason(string path) =>
        path.StartsWith(NotIncludedMarker, StringComparison.Ordinal) ? path : null;

    private static string MediaKind(string? mediaType, string? mime) => mediaType switch
    {
        "voice_message" => "voice",
        "video_message" => "video_message",
        "sticker" => "sticker",
        "animation" => "animation",
        "video_file" => "video",
        "audio_file" => "audio",
        _ when mime is not null && mime.StartsWith("image/", StringComparison.Ordinal) => "photo",
        _ when mime is not null && mime.StartsWith("video/", StringComparison.Ordinal) => "video",
        _ when mime is not null && mime.StartsWith("audio/", StringComparison.Ordinal) => "audio",
        _ => "file",
    };

    /// <summary>
    /// Reads reactions so the stored counts sum to the real total.
    /// </summary>
    /// <remarks>
    /// Telegram reports a total count plus a list of *recent* reactors, which is usually shorter.
    /// Naming the ones it gives and putting the remainder on one anonymous row keeps both facts:
    /// who reacted, and how many did.
    /// </remarks>
    private static List<NormalizedReaction> ReadReactions(JsonElement message)
    {
        var reactions = new List<NormalizedReaction>();

        if (!message.TryGetProperty("reactions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return reactions;
        }

        foreach (var reaction in array.EnumerateArray())
        {
            var emoji = String(reaction, "emoji")
                ?? String(reaction, "document_id")
                ?? String(reaction, "type")
                ?? "?";

            var customEmojiId = String(reaction, "document_id");
            var total = Number(reaction, "count") ?? 0;
            var named = 0L;

            if (reaction.TryGetProperty("recent", out var recent) && recent.ValueKind == JsonValueKind.Array)
            {
                foreach (var actor in recent.EnumerateArray())
                {
                    var identity = ReadIdentity(actor, "from", "from_id");

                    if (identity is null)
                    {
                        continue;
                    }

                    reactions.Add(new NormalizedReaction(
                        emoji, customEmojiId, identity, 1, ReadOptionalTimestamp(actor, "date", "date_unixtime", false)));

                    named++;
                }
            }

            if (total > named)
            {
                reactions.Add(new NormalizedReaction(emoji, customEmojiId, null, total - named, null));
            }
        }

        return reactions;
    }

    /// <summary>
    /// Identifies the message's content, so a re-import can tell "already have this" from
    /// "this was edited since the last export".
    /// </summary>
    private static string ContentHash(
        string plaintext,
        string? entitiesJson,
        string? serviceAction,
        IReadOnlyList<NormalizedMedia> media)
    {
        const char Separator = (char)31;

        var builder = new StringBuilder()
            .Append(plaintext).Append(Separator)
            .Append(entitiesJson).Append(Separator)
            .Append(serviceAction).Append(Separator);

        foreach (var item in media)
        {
            builder.Append(item.ExportPath).Append(Separator);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string ReadId(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id))
        {
            throw new InvalidDataException("Message has no id.");
        }

        return id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => id.GetString()!,
            _ => throw new InvalidDataException($"Message id is not a number or string: {id.ValueKind}."),
        };
    }

    private static string? String(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static long? Number(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }
}
