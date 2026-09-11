using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Archive.Import.GoogleChat;

namespace Archive.Import.Slack;

/// <summary>
/// Slack, from a workspace export.
/// </summary>
/// <remarks>
/// <para>
/// <c>users.json</c> and <c>channels.json</c> at the root, with <c>groups.json</c>,
/// <c>dms.json</c> and <c>mpims.json</c> when the export includes private conversations; then a
/// folder per conversation — channels and private groups by name, direct messages by id — holding
/// one JSON file per day. Every message has a <c>ts</c> unique within its conversation, so this is
/// a strong format: re-exports land on the same rows, and an edit is a revision.
/// </para>
/// <para>
/// Workspace history more than personal history, and the export is an administrator's rather than
/// anyone's own — which is why it never says whose it is. The owner is the one person in every
/// direct conversation when there are enough to tell, and is asked for otherwise.
/// </para>
/// <para>
/// Traps:
/// </para>
/// <list type="bullet">
///   <item><b>Text is Slack's markup</b>: <c>&lt;@U123&gt;</c> for a mention,
///   <c>&lt;#C123|general&gt;</c> for a channel, <c>&lt;https://x|label&gt;</c> for a link, and
///   <c>&amp;amp;</c> for an ampersand. Mentions are resolved to names from <c>users.json</c>, so the
///   text reads the way it did in Slack.</item>
///   <item><b>Subtypes carry meaning.</b> A join, a topic change or a pinned item is a service
///   message; a bot's message has a bot id and a username instead of a user. An unknown subtype
///   stops the import.</item>
///   <item><b>Threads are flat.</b> A reply is a message whose <c>thread_ts</c> is its parent's
///   <c>ts</c>, filed under the day it was sent, not under its parent.</item>
///   <item><b>Files are links that expire.</b> They are recorded by name as attachments the export
///   did not carry.</item>
/// </list>
/// </remarks>
public sealed partial class SlackImporter : IPlatformImporter
{
    public const string PlatformId = "slack";

    private const char Separator = (char)31;

    /// <summary>Subtypes that are Slack reporting something, as service actions.</summary>
    private static readonly Dictionary<string, string> ServiceSubtypes = new(StringComparer.Ordinal)
    {
        ["channel_join"] = "invite_members",
        ["group_join"] = "invite_members",
        ["channel_leave"] = "remove_members",
        ["group_leave"] = "remove_members",
        ["channel_topic"] = "edit_group_title",
        ["group_topic"] = "edit_group_title",
        ["channel_purpose"] = "edit_group_title",
        ["group_purpose"] = "edit_group_title",
        ["channel_name"] = "edit_group_title",
        ["group_name"] = "edit_group_title",
        ["channel_archive"] = "archive",
        ["group_archive"] = "archive",
        ["channel_unarchive"] = "unarchive",
        ["group_unarchive"] = "unarchive",
        ["pinned_item"] = "pin_message",
        ["unpinned_item"] = "unpin_message",
        ["reminder_add"] = "reminder",
        ["bot_add"] = "invite_members",
        ["bot_remove"] = "remove_members",
        ["huddle_thread"] = "phone_call",
    };

    /// <summary>Subtypes that are still somebody's message.</summary>
    private static readonly HashSet<string> MessageSubtypes = new(StringComparer.Ordinal)
    {
        "bot_message",
        "file_share",
        "file_comment",
        "thread_broadcast",
        "me_message",
        "reply_broadcast",
        "slackbot_response",
    };

    [GeneratedRegex(@"<([^<>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    public string Platform => PlatformId;

    public string DisplayName => "Slack";

    public ImportDetection Detect(string path)
    {
        if (!IsExport(path))
        {
            return ImportDetection.No;
        }

        var conversations = Conversations(path);
        var users = Users(path);
        var owner = InferOwner(conversations);

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: owner,
            AccountName: owner is null ? null : users.GetValueOrDefault(owner)?.DisplayName,
            FileCount: conversations.Sum(c => DayFiles(path, c).Length),
            Note: "A Slack export is the workspace's, not yours, and does not say which account is you. "
                + "Shared files are links that expire, so they are recorded by name without their contents.",
            AccountIdIsGuess: true,
            AccountCandidates: owner is null ? Candidates(conversations, users) : [owner]);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (!IsExport(path))
        {
            throw new InvalidDataException($"'{path}' is not a Slack export: it has no users.json and channels.json.");
        }

        var users = Users(path);
        var conversations = Conversations(path);
        var channels = conversations.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);

        var ownerId = StatedOwner(options?.OwnerAccountId, users) ?? InferOwner(conversations);
        NormalizedIdentity? owner = ownerId is null ? null : users.GetValueOrDefault(ownerId) ?? Person(ownerId, null, null);

        if (owner is not null)
        {
            sink.OnOwner(owner);
        }

        foreach (var conversation in conversations)
        {
            ReadConversation(path, conversation, users, channels, owner, sink);
        }
    }

    private static bool IsExport(string path) =>
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "users.json")) &&
        File.Exists(Path.Combine(path, "channels.json"));

    /// <summary>One conversation as the export lists it, and the folder its days are in.</summary>
    private sealed record Conversation(string Id, string Kind, string? Name, string Folder, IReadOnlyList<string> Members);

    private static List<Conversation> Conversations(string path)
    {
        var list = new List<Conversation>();

        // Channels and private groups are filed by name; direct messages by id; group DMs by name.
        foreach (var (file, kind, byName) in new[]
                 {
                     ("channels.json", "group", true),
                     ("groups.json", "group", true),
                     ("dms.json", "dm", false),
                     ("mpims.json", "group", true),
                 })
        {
            var full = Path.Combine(path, file);

            if (!File.Exists(full))
            {
                continue;
            }

            using var document = Parse(full);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"'{full}' is not an array.");
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var id = Text(entry, "id") ?? throw new InvalidDataException($"A conversation in '{full}' has no id.");
                var name = Text(entry, "name");
                var members = entry.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                    : [];

                list.Add(new Conversation(
                    id,
                    kind,
                    file == "mpims.json" ? null : name,
                    byName ? name ?? id : id,
                    members));
            }
        }

        return list;
    }

    private static Dictionary<string, NormalizedIdentity> Users(string path)
    {
        var file = Path.Combine(path, "users.json");
        using var document = Parse(file);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"'{file}' is not an array.");
        }

        var users = new Dictionary<string, NormalizedIdentity>(StringComparer.Ordinal);

        foreach (var user in document.RootElement.EnumerateArray())
        {
            var id = Text(user, "id") ?? throw new InvalidDataException($"A user in '{file}' has no id.");

            var profile = user.TryGetProperty("profile", out var p) ? p : default;
            var realName = Text(user, "real_name") ?? Text(profile, "real_name") ?? Text(profile, "display_name");

            users[id] = Person(id, Text(user, "name"), string.IsNullOrWhiteSpace(realName) ? null : realName);
        }

        return users;
    }

    private static NormalizedIdentity Person(string id, string? handle, string? name) =>
        new(PlatformId, id, handle, name ?? handle ?? id, IsSynthetic: false);

    private static string[] DayFiles(string path, Conversation conversation)
    {
        var folder = Path.Combine(path, conversation.Folder);

        return Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The person in every direct conversation, when there are two or more to compare.</summary>
    private static string? InferOwner(IReadOnlyList<Conversation> conversations)
    {
        var direct = conversations.Where(c => c.Kind == "dm" && c.Members.Count > 0).ToArray();

        if (direct.Length < 2)
        {
            return null;
        }

        var common = direct[0].Members.ToHashSet(StringComparer.Ordinal);

        foreach (var conversation in direct.Skip(1))
        {
            common.IntersectWith(conversation.Members);
        }

        return common.Count == 1 ? common.First() : null;
    }

    /// <summary>The user's answer, as an id, a username or a real name.</summary>
    private static string? StatedOwner(string? stated, Dictionary<string, NormalizedIdentity> users)
    {
        if (string.IsNullOrWhiteSpace(stated))
        {
            return null;
        }

        var answer = stated.Trim();

        return users.Values.FirstOrDefault(u =>
                   u.SourceIdentityId == answer ||
                   string.Equals(u.Handle, answer, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(u.DisplayName, answer, StringComparison.OrdinalIgnoreCase))?.SourceIdentityId
               ?? answer;
    }

    /// <summary>Members of direct conversations, those in the most first — the owner's candidates.</summary>
    private static IReadOnlyList<string> Candidates(IReadOnlyList<Conversation> conversations, Dictionary<string, NormalizedIdentity> users) =>
        [.. conversations
            .Where(c => c.Kind == "dm" || c.Name is null)
            .SelectMany(c => c.Members)
            .GroupBy(m => m, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(g => users.GetValueOrDefault(g.Key)?.Handle ?? g.Key)];

    private void ReadConversation(
        string path,
        Conversation conversation,
        Dictionary<string, NormalizedIdentity> users,
        Dictionary<string, string?> channels,
        NormalizedIdentity? owner,
        IImportSink sink)
    {
        var members = conversation.Members.Select(m => users.GetValueOrDefault(m) ?? Person(m, null, null)).ToArray();
        var others = members.Where(m => owner is null || m.SourceIdentityId != owner.SourceIdentityId).ToArray();

        var title = conversation.Name is { } name
            ? $"#{name}"
            : string.Join(", ", others.Select(o => o.DisplayName));

        var thread = new NormalizedThread(conversation.Id, conversation.Kind, title, members);
        sink.OnThread(thread);

        foreach (var day in DayFiles(path, conversation))
        {
            using var document = Parse(day);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"'{day}' is not an array of messages.");
            }

            foreach (var message in document.RootElement.EnumerateArray())
            {
                if (ReadMessage(day, conversation.Id, message, users, channels) is { } read)
                {
                    sink.OnMessage(thread, read);
                }
            }
        }
    }

    private static NormalizedMessage? ReadMessage(
        string file,
        string conversationId,
        JsonElement message,
        Dictionary<string, NormalizedIdentity> users,
        Dictionary<string, string?> channels)
    {
        var type = Text(message, "type");

        if (type != "message")
        {
            throw new InvalidDataException($"An entry in '{file}' has type '{type}', which this reader does not know.");
        }

        var ts = Text(message, "ts") ?? throw new InvalidDataException($"A message in '{file}' has no ts.");
        var subtype = Text(message, "subtype");

        // A parent whose own message was deleted, kept only so its replies have somewhere to hang.
        if (subtype == "tombstone")
        {
            return null;
        }

        string? service = null;

        if (subtype is not null && !MessageSubtypes.Contains(subtype) && !ServiceSubtypes.TryGetValue(subtype, out service))
        {
            throw new InvalidDataException($"A message in '{file}' has subtype '{subtype}', which this reader does not know.");
        }

        var at = Ts(ts, file);
        var sender = Sender(message, users);
        var text = Render(Text(message, "text") ?? string.Empty, users, channels);
        var media = Files(message);

        string? replyTo = null;

        if (Text(message, "thread_ts") is { } parent && parent != ts)
        {
            replyTo = $"sl/{conversationId}/{parent}";
        }

        string? edited = null;

        if (message.TryGetProperty("edited", out var e) && e.ValueKind == JsonValueKind.Object && Text(e, "ts") is { } editedTs)
        {
            edited = Ts(editedTs, file).ToString("O", CultureInfo.InvariantCulture);
        }

        return new NormalizedMessage
        {
            Uid = $"sl/{conversationId}/{ts}",
            SourceThreadId = conversationId,
            Kind = service is null ? "message" : "service",
            Sender = sender,
            ServiceAction = service,
            SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
            SentAtUnix = at.ToUnixTimeSeconds(),
            Plaintext = text,
            ContentHash = Hash($"{text}{Separator}{string.Join(Separator, media.Select(m => m.OriginalFilename))}"),
            ReplyToUid = replyTo,
            EditedAtUtc = edited,
            RawJson = message.GetRawText(),
            Media = media,
            Reactions = Reactions(message, users),
        };
    }

    /// <summary><c>1615757463.000200</c>: seconds, and a sequence that makes it unique.</summary>
    private static DateTimeOffset Ts(string ts, string file)
    {
        var dot = ts.IndexOf('.', StringComparison.Ordinal);
        var seconds = dot < 0 ? ts : ts[..dot];

        return long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : throw new InvalidDataException($"'{ts}' in '{file}' is not a Slack timestamp.");
    }

    /// <summary>A user, or a bot by its id and the name it posted under.</summary>
    private static NormalizedIdentity? Sender(JsonElement message, Dictionary<string, NormalizedIdentity> users)
    {
        if (Text(message, "user") is { } user)
        {
            if (users.TryGetValue(user, out var known))
            {
                return known;
            }

            var profile = message.TryGetProperty("user_profile", out var p) ? p : default;

            return Person(user, Text(profile, "name"), Text(profile, "real_name"));
        }

        var username = Text(message, "username");

        if (Text(message, "bot_id") is { } bot)
        {
            return new NormalizedIdentity(PlatformId, $"bot:{bot}", username, username ?? bot, IsSynthetic: false);
        }

        return username is null ? null : new NormalizedIdentity(PlatformId, null, null, username, IsSynthetic: true);
    }

    /// <summary>
    /// Slack's markup, as it read in Slack.
    /// </summary>
    internal static string Render(
        string text, IReadOnlyDictionary<string, NormalizedIdentity> users, IReadOnlyDictionary<string, string?> channels)
    {
        var rendered = Markup().Replace(text, match =>
        {
            var inner = match.Groups[1].Value;
            var bar = inner.IndexOf('|', StringComparison.Ordinal);
            var target = bar < 0 ? inner : inner[..bar];
            var label = bar < 0 ? null : inner[(bar + 1)..];

            return target[0] switch
            {
                '@' => "@" + (label ?? users.GetValueOrDefault(target[1..])?.DisplayName ?? target[1..]),
                '#' => "#" + (label ?? channels.GetValueOrDefault(target[1..]) ?? target[1..]),
                '!' => target.StartsWith("!subteam^", StringComparison.Ordinal)
                    ? label ?? "@team"
                    : "@" + (label ?? target[1..]),
                _ => label ?? (target.StartsWith("mailto:", StringComparison.Ordinal) ? target[7..] : target),
            };
        });

        return WebUtility.HtmlDecode(rendered);
    }

    private static List<NormalizedMedia> Files(JsonElement message)
    {
        var media = new List<NormalizedMedia>();

        if (!message.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return media;
        }

        foreach (var file in files.EnumerateArray())
        {
            var name = Text(file, "name") ?? Text(file, "title");
            var url = Text(file, "url_private") ?? Text(file, "permalink") ?? name ?? "file";

            media.Add(new NormalizedMedia(
                url, GoogleChatImporter.MediaKindOf(name), name, Text(file, "mimetype"),
                MissingReason: "Slack exports link to shared files, and the links expire",
                StickerEmoji: null, Width: null, Height: null, DurationSeconds: null));
        }

        return media;
    }

    /// <summary>Reactions are shortcodes, kept as <c>:name:</c>; one row per named reactor, and the rest as a count.</summary>
    private static List<NormalizedReaction> Reactions(JsonElement message, Dictionary<string, NormalizedIdentity> users)
    {
        var reactions = new List<NormalizedReaction>();

        if (!message.TryGetProperty("reactions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return reactions;
        }

        foreach (var reaction in list.EnumerateArray())
        {
            if (Text(reaction, "name") is not { } name)
            {
                continue;
            }

            var emoji = $":{name}:";
            var reactors = reaction.TryGetProperty("users", out var u) && u.ValueKind == JsonValueKind.Array
                ? u.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : [];

            foreach (var reactor in reactors)
            {
                reactions.Add(new NormalizedReaction(emoji, null, users.GetValueOrDefault(reactor) ?? Person(reactor, null, null), 1, null));
            }

            var count = reaction.TryGetProperty("count", out var c) && c.TryGetInt64(out var n) ? n : reactors.Length;

            if (count > reactors.Length)
            {
                reactions.Add(new NormalizedReaction(emoji, null, null, count - reactors.Length, null));
            }
        }

        return reactions;
    }

    private static JsonDocument Parse(string file) =>
        JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true });

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
