using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archive.Import.Synthetic;

/// <summary>One account as an export names it.</summary>
/// <param name="Name">What the export writes in <c>from</c> or <c>actor</c>.</param>
/// <param name="FromId">
/// What it writes in <c>from_id</c>, prefix and all. Null for the old exports that give a name and
/// nothing else (§2), which is what makes a synthetic identity.
/// </param>
public sealed record TelegramAccount(string Name, string? FromId)
{
    public static TelegramAccount User(long id, string name) =>
        new(name, "user" + id.ToString(CultureInfo.InvariantCulture));

    public static TelegramAccount Channel(long id, string name) =>
        new(name, "channel" + id.ToString(CultureInfo.InvariantCulture));

    /// <summary>An id with no prefix at all, as older exports wrote them.</summary>
    public static TelegramAccount Unprefixed(long id, string name) =>
        new(name, id.ToString(CultureInfo.InvariantCulture));

    /// <summary>A name with no id — a deleted account, or a very old export.</summary>
    public static TelegramAccount NameOnly(string name) => new(name, null);

    /// <summary>Any <c>from_id</c> at all, including prefixes this app does not know.</summary>
    public static TelegramAccount Raw(string fromId, string name) => new(name, fromId);
}

/// <summary>
/// Builds a Telegram Desktop JSON export, as a real folder on disk.
/// </summary>
/// <remarks>
/// <para>
/// Tests need real files in a real folder — reading an export folder is most of what the importer
/// does, and a test that hands it a string would not exercise that at all. What they must not need
/// is an export <em>committed to the repository</em>: an archive is private correspondence, and a
/// checked-in file shaped exactly like one is a standing invitation to replace it with the real
/// thing. So the shapes are described here, in code, and written to a temporary folder when a test
/// runs.
/// </para>
/// <para>
/// It lives in the library beside <see cref="SyntheticExport"/> and for the same reason: the CLI,
/// the performance tests and the unit tests then share one statement of what an export looks like.
/// Where <see cref="SyntheticExport"/> generates <em>volume</em>, this generates <em>shapes</em> —
/// one awkward corner of §2 at a time, named so a test reads as the trap it is covering.
/// </para>
/// <para>
/// It cannot confirm the format, only pin it. A builder written from the same misreading as a
/// reader will agree with it (decisions.md D22); only a real export settles what the format is.
/// </para>
/// </remarks>
public sealed class TelegramExportBuilder
{
    private readonly JsonObject _root;
    private readonly JsonArray? _chats;
    private readonly JsonArray? _leftChats;

    private TelegramExportBuilder(JsonObject root, JsonArray? chats, JsonArray? leftChats)
    {
        _root = root;
        _chats = chats;
        _leftChats = leftChats;
    }

    /// <summary>A full export: <c>personal_information</c> plus <c>chats.list</c>.</summary>
    public static TelegramExportBuilder Full()
    {
        var chats = new JsonArray();
        var left = new JsonArray();

        var root = new JsonObject
        {
            ["about"] = "This is a full export of your Telegram data.",
            ["chats"] = new JsonObject { ["about"] = "Chats.", ["list"] = chats },
            ["left_chats"] = new JsonObject { ["about"] = "Chats you left.", ["list"] = left },
        };

        return new TelegramExportBuilder(root, chats, left);
    }

    /// <summary>
    /// A single-chat export, whose chat fields sit at the root.
    /// </summary>
    /// <remarks>
    /// Telegram Desktop can export one conversation on its own, and such a file has no
    /// personal_information at all — so the owner has to come from somewhere else.
    /// </remarks>
    public static TelegramExportBuilder OneChat(
        string? name, string type, long? id, Action<TelegramChatBuilder> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var root = new JsonObject();
        WriteChatHeader(root, name, type, id);

        var array = new JsonArray();
        root["messages"] = array;
        messages(new TelegramChatBuilder(array));

        return new TelegramExportBuilder(root, chats: null, leftChats: null);
    }

    /// <summary>Seeds the owner the way a real export does (§2).</summary>
    public TelegramExportBuilder Owner(
        long userId, string firstName, string? lastName = null, string? username = null, string? phoneNumber = null)
    {
        var personal = new JsonObject { ["user_id"] = userId, ["first_name"] = firstName };

        if (lastName is not null)
        {
            personal["last_name"] = lastName;
        }

        if (phoneNumber is not null)
        {
            personal["phone_number"] = phoneNumber;
        }

        if (username is not null)
        {
            personal["username"] = username;
        }

        // Ordered ahead of chats, where a real export puts it: the reader stops as soon as it has
        // this block, and a test that put it last would not be exercising that at all.
        Prepend("personal_information", personal);

        return this;
    }

    public TelegramExportBuilder Chat(string? name, string type, long? id, Action<TelegramChatBuilder> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Add(_chats, name, type, id, messages);

        return this;
    }

    /// <summary>
    /// A chat in <c>left_chats</c>.
    /// </summary>
    /// <remarks>
    /// These are whole conversations with people you no longer share a chat with. Treating the
    /// section as noise loses entire relationships from the archive.
    /// </remarks>
    public TelegramExportBuilder LeftChat(string? name, string type, long? id, Action<TelegramChatBuilder> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Add(_leftChats, name, type, id, messages);

        return this;
    }

    public string Json() => _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes the export into <paramref name="folder"/> and returns the folder.</summary>
    public string Write(string folder, string fileName = "result.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), Json());

        return folder;
    }

    private static void Add(
        JsonArray? list, string? name, string type, long? id, Action<TelegramChatBuilder> messages)
    {
        if (list is null)
        {
            throw new InvalidOperationException("A single-chat export holds exactly one chat.");
        }

        var chat = new JsonObject();
        WriteChatHeader(chat, name, type, id);

        var array = new JsonArray();
        chat["messages"] = array;
        messages(new TelegramChatBuilder(array));

        list.Add(chat);
    }

    private static void WriteChatHeader(JsonObject chat, string? name, string type, long? id)
    {
        if (name is not null)
        {
            chat["name"] = name;
        }

        chat["type"] = type;

        // Older exports have no chat id at all, and the reader falls back to the name.
        if (id is not null)
        {
            chat["id"] = id.Value;
        }
    }

    private void Prepend(string property, JsonNode value)
    {
        var existing = _root.ToList();
        _root.Clear();
        _root[property] = value;

        foreach (var pair in existing)
        {
            _root[pair.Key] = pair.Value;
        }
    }
}

/// <summary>The messages of one chat.</summary>
public sealed class TelegramChatBuilder(JsonArray messages)
{
    public TelegramChatBuilder Message(
        long id,
        DateTimeOffset at,
        TelegramAccount from,
        string text,
        Action<TelegramMessageBuilder>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(from);

        var message = new JsonObject { ["id"] = id, ["type"] = "message" };
        WriteTimestamp(message, at);

        message["from"] = from.Name;

        if (from.FromId is not null)
        {
            message["from_id"] = from.FromId;
        }

        Finish(message, text, extra);

        return this;
    }

    /// <summary>
    /// A service message.
    /// </summary>
    /// <remarks>
    /// §2: these carry <c>actor</c>/<c>actor_id</c> and an <c>action</c>, never
    /// <c>from</c>/<c>from_id</c>. Reading them through the ordinary path is what produces phantom
    /// people named after actions.
    /// </remarks>
    public TelegramChatBuilder Service(
        long id,
        DateTimeOffset at,
        TelegramAccount actor,
        string action,
        Action<TelegramMessageBuilder>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var message = new JsonObject { ["id"] = id, ["type"] = "service" };
        WriteTimestamp(message, at);

        message["actor"] = actor.Name;

        if (actor.FromId is not null)
        {
            message["actor_id"] = actor.FromId;
        }

        message["action"] = action;

        Finish(message, string.Empty, extra);

        return this;
    }

    /// <summary>
    /// Writes both forms of the timestamp, the way a real export does.
    /// </summary>
    /// <remarks>
    /// <c>date_unixtime</c> is the instant; <c>date</c> is the sender's wall clock with no offset
    /// attached. The difference between them is the offset the sender was in, which the archive
    /// keeps as behavioural signal (§7) — so a builder that wrote only one of them would make that
    /// untestable.
    /// </remarks>
    internal static void WriteTimestamp(JsonObject message, DateTimeOffset at)
    {
        message["date"] = at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        message["date_unixtime"] = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }

    private void Finish(JsonObject message, string text, Action<TelegramMessageBuilder>? extra)
    {
        var builder = new TelegramMessageBuilder(message);
        builder.Text(text);

        extra?.Invoke(builder);

        messages.Add(message);
    }
}

/// <summary>Everything one message can carry beyond a sender, a time and some text.</summary>
public sealed class TelegramMessageBuilder(JsonObject message)
{
    /// <summary>The sentinel Telegram writes when the export excluded the file itself.</summary>
    public const string NotIncluded =
        "(File not included. Change data exporting settings to download.)";

    /// <summary>
    /// Sets <c>text_entities</c> from plain text, and mirrors it into <c>text</c>.
    /// </summary>
    /// <remarks>
    /// Called for every message; <see cref="Entities"/> and the <c>RawTextField</c> overloads
    /// override it where a test is about the difference between the two fields.
    /// </remarks>
    public TelegramMessageBuilder Text(string text)
    {
        message["text"] = text;
        message["text_entities"] = text.Length == 0
            ? new JsonArray()
            : new JsonArray(new JsonObject { ["type"] = "plain", ["text"] = text });

        return this;
    }

    /// <summary>
    /// Sets <c>text_entities</c> explicitly — links, bold, code and the rest.
    /// </summary>
    /// <remarks>
    /// Leaves <c>text</c> alone, so a test can make the two disagree. §2's first trap is that
    /// <c>text</c> must never be read, and it is only provable when the two differ.
    /// </remarks>
    public TelegramMessageBuilder Entities(params (string Type, string Text)[] entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var array = new JsonArray();

        foreach (var (type, text) in entities)
        {
            array.Add(new JsonObject { ["type"] = type, ["text"] = text });
        }

        message["text_entities"] = array;

        return this;
    }

    /// <summary>Sets the polymorphic <c>text</c> field to a string.</summary>
    public TelegramMessageBuilder RawTextField(string text)
    {
        message["text"] = text;

        return this;
    }

    /// <summary>
    /// Sets <c>text</c> to the array form — strings mixed with objects.
    /// </summary>
    /// <remarks>§2: the other half of the polymorphic field, and equally never read.</remarks>
    public TelegramMessageBuilder RawTextArray(params object[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var array = new JsonArray();

        foreach (var part in parts)
        {
            array.Add(part switch
            {
                string plain => JsonValue.Create(plain),
                (string type, string text) => new JsonObject { ["type"] = type, ["text"] = text },
                _ => throw new ArgumentException(
                    "A text part is either a string or a (type, text) pair.", nameof(parts)),
            });
        }

        message["text"] = array;

        return this;
    }

    public TelegramMessageBuilder ReplyTo(long messageId)
    {
        message["reply_to_message_id"] = messageId;

        return this;
    }

    public TelegramMessageBuilder Edited(DateTimeOffset at)
    {
        message["edited"] = at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        message["edited_unixtime"] = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        return this;
    }

    public TelegramMessageBuilder ForwardedFrom(string origin)
    {
        message["forwarded_from"] = origin;

        return this;
    }

    public TelegramMessageBuilder ViaBot(string bot)
    {
        message["via_bot"] = bot;

        return this;
    }

    /// <summary>A photo. Pass <see cref="NotIncluded"/> as the path for one the export omitted.</summary>
    public TelegramMessageBuilder Photo(string path, long? width = null, long? height = null)
    {
        message["photo"] = path;
        Dimensions(width, height);

        return this;
    }

    /// <summary>Any non-photo attachment: a file, a voice note, a video, a document.</summary>
    public TelegramMessageBuilder File(
        string path,
        string? mediaType = null,
        string? mime = null,
        long? durationSeconds = null,
        long? width = null,
        long? height = null,
        string? fileName = null)
    {
        message["file"] = path;

        if (mediaType is not null)
        {
            message["media_type"] = mediaType;
        }

        if (mime is not null)
        {
            message["mime_type"] = mime;
        }

        if (durationSeconds is not null)
        {
            message["duration_seconds"] = durationSeconds.Value;
        }

        if (fileName is not null)
        {
            message["file_name"] = fileName;
        }

        Dimensions(width, height);

        return this;
    }

    public TelegramMessageBuilder Sticker(string path, string emoji, long size = 512)
    {
        File(path, "sticker", "image/webp", width: size, height: size);
        message["sticker_emoji"] = emoji;

        return this;
    }

    public TelegramMessageBuilder Thumbnail(string path)
    {
        message["thumbnail"] = path;

        return this;
    }

    /// <summary>
    /// One reaction bucket.
    /// </summary>
    /// <remarks>
    /// <paramref name="count"/> is the total and <paramref name="recent"/> is usually shorter —
    /// Telegram names only the most recent reactors. The gap between the two is the thing worth
    /// testing, so it is expressed rather than derived.
    /// </remarks>
    public TelegramMessageBuilder Reaction(
        string emoji, long count, params (TelegramAccount Who, DateTimeOffset At)[] recent) =>
        AddReaction(new JsonObject { ["type"] = "emoji", ["count"] = count, ["emoji"] = emoji }, recent);

    public TelegramMessageBuilder CustomReaction(
        string documentId, long count, params (TelegramAccount Who, DateTimeOffset At)[] recent) =>
        AddReaction(
            new JsonObject { ["type"] = "custom_emoji", ["count"] = count, ["document_id"] = documentId },
            recent);

    /// <summary>Anything else a real export carries that nothing here has needed yet.</summary>
    public TelegramMessageBuilder Field(string name, JsonNode? value)
    {
        message[name] = value;

        return this;
    }

    private TelegramMessageBuilder AddReaction(
        JsonObject reaction, (TelegramAccount Who, DateTimeOffset At)[] recent)
    {
        ArgumentNullException.ThrowIfNull(recent);

        if (recent.Length > 0)
        {
            var array = new JsonArray();

            foreach (var (who, at) in recent)
            {
                var actor = new JsonObject { ["from"] = who.Name };

                if (who.FromId is not null)
                {
                    actor["from_id"] = who.FromId;
                }

                TelegramChatBuilder.WriteTimestamp(actor, at);
                array.Add(actor);
            }

            reaction["recent"] = array;
        }

        if (message["reactions"] is not JsonArray reactions)
        {
            reactions = [];
            message["reactions"] = reactions;
        }

        reactions.Add(reaction);

        return this;
    }

    private void Dimensions(long? width, long? height)
    {
        if (width is not null)
        {
            message["width"] = width.Value;
        }

        if (height is not null)
        {
            message["height"] = height.Value;
        }
    }
}
