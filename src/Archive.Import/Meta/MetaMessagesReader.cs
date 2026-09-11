using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Archive.Import.Meta;

/// <summary>Which Meta product a download came from.</summary>
public enum MetaProduct
{
    Facebook,
    Instagram,
}

/// <summary>
/// The message JSON Facebook Messenger and Instagram share.
/// </summary>
/// <remarks>
/// <para>
/// Both products write <c>messages/&lt;folder&gt;/&lt;thread&gt;/message_N.json</c>, where the
/// folder is <c>inbox</c>, <c>archived_threads</c>, <c>filtered_threads</c>,
/// <c>message_requests</c> or <c>e2ee_cutover</c>, and <c>messages</c> sits either at the root of
/// the download or under <c>your_facebook_activity</c> / <c>your_instagram_activity</c>. The same
/// JSON, but two platforms: a name on Instagram is not the same account as that name on Facebook,
/// so each product is its own importer over this one reader (the roadmap's G2).
/// </para>
/// <para>
/// Traps, all of them the format's own:
/// </para>
/// <list type="bullet">
///   <item><b>Text is double-encoded.</b> Every string is its UTF-8 bytes written as
///   <c>\u00XX</c> escapes, so "Марина" arrives as "ÐœÐ°Ñ€Ð¸Ð½Ð°". It is repaired by reading
///   the characters back as bytes — only when every one fits in a byte and the bytes are valid
///   UTF-8, so a string that was never broken is left alone.</item>
///   <item><b>There are no message ids.</b> The uid is the thread, the millisecond and a hash of
///   the sender and content, with an occurrence count for exact repeats. An edit is therefore a new
///   message; Meta's messages are close to immutable, which is why this is tolerable here.</item>
///   <item><b>There are no account ids either</b> — only display names. Every person is keyed by
///   name and flagged as such, and merging is left to the user (D29).</item>
///   <item><b>Files run newest first</b>, and so do the messages inside them. They are sorted by
///   time before anything is emitted.</item>
///   <item><b>A thread moves</b> between <c>inbox</c> and <c>archived_threads</c> over its life,
///   so it is keyed by its own folder name, not by the path.</item>
/// </list>
/// </remarks>
public static class MetaMessagesReader
{
    private static readonly string[] Folders =
        ["inbox", "archived_threads", "filtered_threads", "message_requests", "e2ee_cutover"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The <c>messages</c> folder for a product, and whether the download says which product it is.</summary>
    internal static (string? Messages, bool Marked) Locate(string path, MetaProduct product)
    {
        if (!Directory.Exists(path))
        {
            return (null, false);
        }

        var activity = product == MetaProduct.Facebook
            ? new[] { "your_facebook_activity", "your_activity_across_facebook" }
            : ["your_instagram_activity"];

        foreach (var folder in activity)
        {
            var messages = Path.Combine(path, folder, "messages");

            if (HasThreads(messages))
            {
                return (messages, true);
            }
        }

        // The older layout: messages at the root, with nothing in the path saying which product.
        // What else is in the download does.
        var root = HasThreads(Path.Combine(path, "messages")) ? Path.Combine(path, "messages")
            : HasThreads(path) && IsMessagesFolder(path) ? path
            : null;

        if (root is null)
        {
            return (null, false);
        }

        var download = IsMessagesFolder(path) ? Path.GetDirectoryName(path) ?? path : path;

        return (root, product == MetaProduct.Facebook ? LooksLikeFacebook(download) : LooksLikeInstagram(download));
    }

    private static bool IsMessagesFolder(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("messages", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeInstagram(string download) =>
        Directory.Exists(Path.Combine(download, "your_instagram_activity")) ||
        File.Exists(Path.Combine(download, "personal_information", "personal_information", "personal_information.json")) ||
        Directory.Exists(Path.Combine(download, "connections", "followers_and_following")) ||
        Directory.Exists(Path.Combine(download, "followers_and_following"));

    internal static bool LooksLikeFacebook(string download) =>
        Directory.Exists(Path.Combine(download, "your_facebook_activity")) ||
        Directory.Exists(Path.Combine(download, "profile_information")) ||
        File.Exists(Path.Combine(download, "personal_information", "profile_information", "profile_information.json"));

    private static bool HasThreads(string messages) =>
        Directory.Exists(messages) && Folders.Any(f => Directory.Exists(Path.Combine(messages, f)));

    internal static string[] ThreadFiles(string messages) =>
        [.. Folders
            .Select(f => Path.Combine(messages, f))
            .Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "message_*.json", SearchOption.AllDirectories))];

    /// <summary>The owner's name, as the download's own profile states it.</summary>
    internal static string? StatedOwner(string download, MetaProduct product)
    {
        if (product == MetaProduct.Facebook)
        {
            foreach (var file in new[]
                     {
                         Path.Combine(download, "personal_information", "profile_information", "profile_information.json"),
                         Path.Combine(download, "profile_information", "profile_information.json"),
                     })
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                using var document = Parse(file);

                foreach (var key in new[] { "profile_v2", "profile" })
                {
                    if (document.RootElement.TryGetProperty(key, out var profile) &&
                        profile.TryGetProperty("name", out var name) &&
                        Text(name, "full_name") is { } full)
                    {
                        return full;
                    }
                }
            }

            return null;
        }

        var personal = Path.Combine(download, "personal_information", "personal_information", "personal_information.json");

        if (!File.Exists(personal))
        {
            return null;
        }

        using var info = Parse(personal);

        if (info.RootElement.TryGetProperty("profile_user", out var users) &&
            users.ValueKind == JsonValueKind.Array &&
            users.GetArrayLength() > 0 &&
            users[0].TryGetProperty("string_map_data", out var map) &&
            map.TryGetProperty("Name", out var nameEntry))
        {
            return Text(nameEntry, "value");
        }

        return null;
    }

    /// <summary>
    /// Repairs Meta's double-encoded strings.
    /// </summary>
    /// <remarks>
    /// Meta writes each UTF-8 byte of a string as its own <c>\u00XX</c> escape, so after JSON
    /// decoding every character is one byte of the real text. Reading them back as bytes restores
    /// it — but only when every character fits in a byte and the result is valid UTF-8. A string
    /// that was written correctly fails one of those, and is returned untouched.
    /// </remarks>
    internal static string Repair(string value)
    {
        var bytes = new byte[value.Length];
        var anyHigh = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c > 0xFF)
            {
                return value;
            }

            anyHigh |= c > 0x7F;
            bytes[i] = (byte)c;
        }

        if (!anyHigh)
        {
            return value;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return value;
        }
    }

    /// <summary>A person on this product: a name, since Meta exports carry nothing else.</summary>
    internal static NormalizedIdentity Person(string platform, string name) =>
        new(platform, null, null, name, IsSynthetic: true);

    /// <summary>
    /// Whether this download holds the product's messages, and whose they are.
    /// </summary>
    /// <remarks>
    /// Instagram is only claimed when the download says it is Instagram's. Messenger also claims a
    /// bare <c>messages</c> folder with nothing marking it either way, at Possible — the older
    /// layout, and far the commoner of the two.
    /// </remarks>
    internal static ImportDetection Detect(string path, MetaProduct product)
    {
        var (messages, marked) = Locate(path, product);

        if (messages is null)
        {
            return ImportDetection.No;
        }

        var download = DownloadRoot(messages);

        if (!marked && (product == MetaProduct.Instagram || LooksLikeInstagram(download)))
        {
            return ImportDetection.No;
        }

        var files = ThreadFiles(messages);

        if (files.Length == 0)
        {
            return ImportDetection.No;
        }

        var owner = StatedOwner(download, product);

        var note = "Meta downloads carry no message or account ids: people are matched by name, and a "
            + "message edited between two downloads arrives as a second one.";

        if (!marked)
        {
            note += " Nothing here says whether this came from Facebook or Instagram, so it is read as Messenger.";
        }

        return new ImportDetection(
            marked ? ImportConfidence.Certain : ImportConfidence.Possible,
            AccountId: null,
            AccountName: owner,
            FileCount: files.Length,
            Note: note,
            AccountIdIsGuess: owner is null,
            AccountCandidates: owner is null ? Candidates(messages) : null);
    }

    /// <summary>Every thread in the download, grouped from its <c>message_N.json</c> files.</summary>
    internal static IEnumerable<(string Folder, string[] Files)> Threads(string messages) =>
        ThreadFiles(messages)
            .GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.Ordinal)
            .OrderBy(g => Path.GetFileName(g.Key), StringComparer.Ordinal)
            .Select(g => (g.Key, g.OrderBy(PartNumber).ToArray()));

    private static int PartNumber(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var underscore = name.LastIndexOf('_');

        return int.TryParse(name[(underscore + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static JsonDocument Parse(string file) =>
        JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true });

    /// <summary>
    /// Reads one product's download and pushes it at the sink.
    /// </summary>
    internal static void Read(
        string path, MetaProduct product, string platform, string uidPrefix, IImportSink sink, ImportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var (messages, _) = Locate(path, product);

        if (messages is null)
        {
            throw new InvalidDataException(
                $"'{path}' has no messages folder. Point at the folder the {product} download unpacked into.");
        }

        var download = DownloadRoot(messages);
        var threads = Threads(messages).ToArray();

        // Parsed once and kept: the owner has to be known before the first message is emitted,
        // and inferring it needs every thread's participant list.
        var parsed = new List<(string Folder, List<JsonDocument> Parts)>(threads.Length);

        try
        {
            foreach (var (folder, files) in threads)
            {
                parsed.Add((folder, [.. files.Select(Parse)]));
            }

            var ownerName = options?.OwnerAccountId is { Length: > 0 } stated
                ? stated.Trim()
                : StatedOwner(download, product) ?? InferOwner(parsed.Select(p => ParticipantNames(p.Parts)).ToArray());

            NormalizedIdentity? owner = ownerName is null ? null : Person(platform, ownerName);

            if (owner is not null)
            {
                sink.OnOwner(owner);
            }

            foreach (var (folder, parts) in parsed)
            {
                ReadThread(path, folder, parts, platform, uidPrefix, owner, sink);
            }
        }
        finally
        {
            foreach (var (_, parts) in parsed)
            {
                foreach (var document in parts)
                {
                    document.Dispose();
                }
            }
        }
    }

    /// <summary>The download's root: the folder above <c>messages</c>, above the activity folder if there is one.</summary>
    internal static string DownloadRoot(string messages)
    {
        var parent = Path.GetDirectoryName(messages) ?? messages;
        var name = Path.GetFileName(parent);

        return name.StartsWith("your_", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(parent) ?? parent
            : parent;
    }

    /// <summary>
    /// The one name in every thread, when there is exactly one and at least two threads to compare.
    /// </summary>
    /// <remarks>
    /// With a single thread both people are in every thread, and nothing is claimed — the preview
    /// asks instead. Getting the owner wrong puts your own messages on the far side of all of them.
    /// </remarks>
    internal static string? InferOwner(IReadOnlyList<IReadOnlyList<string>> participants)
    {
        if (participants.Count < 2)
        {
            return null;
        }

        var common = participants[0].ToHashSet(StringComparer.Ordinal);

        foreach (var list in participants.Skip(1))
        {
            common.IntersectWith(list);
        }

        return common.Count == 1 ? common.First() : null;
    }

    internal static IReadOnlyList<string> ParticipantNames(IReadOnlyList<JsonDocument> parts)
    {
        var names = new List<string>();

        foreach (var part in parts)
        {
            if (!part.RootElement.TryGetProperty("participants", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var participant in list.EnumerateArray())
            {
                if (Text(participant, "name") is { } name && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Participant names, most frequent first — the owner's candidates.
    /// </summary>
    /// <remarks>
    /// From the first twenty threads only: detection is cheap by contract, and the owner is in
    /// every thread, so twenty is plenty to put them at the top.
    /// </remarks>
    internal static IReadOnlyList<string> Candidates(string messages)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (_, files) in Threads(messages).Take(20))
        {
            using var first = Parse(files[0]);

            foreach (var name in ParticipantNames([first]))
            {
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }

        return [.. counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(5).Select(p => p.Key)];
    }

    private static void ReadThread(
        string exportRoot,
        string folder,
        List<JsonDocument> parts,
        string platform,
        string uidPrefix,
        NormalizedIdentity? owner,
        IImportSink sink)
    {
        var threadId = Path.GetFileName(folder);
        var header = parts[0].RootElement;
        var names = ParticipantNames(parts);

        var type = Text(header, "thread_type");
        var kind = type switch
        {
            "RegularGroup" => "group",
            "Regular" => "dm",
            _ when names.Count > 2 => "group",
            _ => "dm",
        };

        var title = Text(header, "title");

        if (kind == "dm" && string.IsNullOrWhiteSpace(title))
        {
            title = names.FirstOrDefault(n => owner is null || n != owner.DisplayName);
        }

        var roster = names.Select(n => Person(platform, n)).ToArray();
        var thread = new NormalizedThread(threadId, kind, title, roster);

        sink.OnThread(thread);

        var messages = parts
            .Where(p => p.RootElement.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array)
            .SelectMany(p => p.RootElement.GetProperty("messages").EnumerateArray())
            .Select(m => (Message: m, At: TimestampMs(m, folder)))
            .OrderBy(m => m.At)
            .ToArray();

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (message, at) in messages)
        {
            sink.OnMessage(thread, ReadMessage(exportRoot, folder, threadId, platform, uidPrefix, message, at, seen));
        }
    }

    private static long TimestampMs(JsonElement message, string folder)
    {
        if (message.TryGetProperty("timestamp_ms", out var value) && value.TryGetInt64(out var ms))
        {
            return ms;
        }

        throw new InvalidDataException($"A message in '{folder}' has no timestamp_ms.");
    }

    private static NormalizedMessage ReadMessage(
        string exportRoot,
        string folder,
        string threadId,
        string platform,
        string uidPrefix,
        JsonElement message,
        long ms,
        Dictionary<string, int> seen)
    {
        var senderName = Text(message, "sender_name")
            ?? throw new InvalidDataException($"A message in '{folder}' has no sender_name.");

        var sender = Person(platform, senderName);
        var content = Text(message, "content") ?? string.Empty;
        var media = Media(exportRoot, message);

        var share = message.TryGetProperty("share", out var s) && s.ValueKind == JsonValueKind.Object
            ? Text(s, "link")
            : null;

        // A shared link with no words of its own is the link.
        if (content.Length == 0 && share is not null)
        {
            content = share;
        }

        var type = Text(message, "type");

        var service = type switch
        {
            null or "Generic" or "Share" => null,
            "Call" => "phone_call",
            "Subscribe" => "invite_members",
            "Unsubscribe" => "remove_members",
            _ => throw new InvalidDataException(
                $"A message in '{folder}' has type '{type}', which this reader does not know."),
        };

        // Unsent messages keep their place in the conversation with no words, which is what the
        // export leaves of them.
        if (message.TryGetProperty("is_unsent", out var unsent) && unsent.ValueKind == JsonValueKind.True)
        {
            content = string.Empty;
        }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms);

        var identity = new StringBuilder()
            .Append(senderName).Append(Separator)
            .Append(content).Append(Separator)
            .AppendJoin(Separator, media.Select(m => m.ExportPath))
            .ToString();

        var key = $"{ms}/{Hash(identity)[..12]}";
        var occurrence = seen.GetValueOrDefault(key);
        seen[key] = occurrence + 1;

        return new NormalizedMessage
        {
            Uid = $"{uidPrefix}/{threadId}/{key}/{occurrence}",
            SourceThreadId = threadId,
            Kind = service is null ? "message" : "service",
            Sender = sender,
            ServiceAction = service,
            SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
            SentAtUnix = at.ToUnixTimeSeconds(),
            Plaintext = content,
            ContentHash = Hash(identity),
            RawJson = message.GetRawText(),
            Media = media,
            Reactions = Reactions(platform, message),
        };
    }

    private const char Separator = (char)31;

    private static readonly (string Property, string Kind)[] MediaProperties =
    [
        ("photos", "photo"),
        ("videos", "video"),
        ("gifs", "animation"),
        ("audio_files", "voice"),
        ("files", "file"),
    ];

    private static List<NormalizedMedia> Media(string exportRoot, JsonElement message)
    {
        var media = new List<NormalizedMedia>();

        foreach (var (property, kind) in MediaProperties)
        {
            if (!message.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "uri") is { } uri)
                {
                    media.Add(Attachment(exportRoot, uri, kind));
                }
            }
        }

        if (message.TryGetProperty("sticker", out var sticker) && Text(sticker, "uri") is { } stickerUri)
        {
            media.Add(Attachment(exportRoot, stickerUri, "sticker"));
        }

        return media;
    }

    /// <summary>
    /// One attachment, its path made relative to the folder the user pointed at.
    /// </summary>
    /// <remarks>
    /// Meta writes paths from the download's root — <c>messages/inbox/…</c> or
    /// <c>your_facebook_activity/messages/inbox/…</c>. Someone who pointed at the <c>messages</c>
    /// folder itself would otherwise lose every attachment to a doubled prefix, so leading segments
    /// are dropped until the file is found. A path that never resolves is kept as written and
    /// counted as absent, which is what an export without media produces too.
    /// </remarks>
    private static NormalizedMedia Attachment(string exportRoot, string uri, string kind)
    {
        var path = uri.Replace('\\', '/');

        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return new NormalizedMedia(
                path, kind, OriginalFilename: null, Mime: null,
                MissingReason: "Meta linked this attachment rather than including it",
                StickerEmoji: null, Width: null, Height: null, DurationSeconds: null);
        }

        var candidate = path;

        while (!File.Exists(Path.Combine(exportRoot, candidate)))
        {
            var slash = candidate.IndexOf('/', StringComparison.Ordinal);

            if (slash < 0)
            {
                candidate = path;
                break;
            }

            candidate = candidate[(slash + 1)..];
        }

        return new NormalizedMedia(
            candidate, kind, Path.GetFileName(path), Mime: null, MissingReason: null,
            StickerEmoji: null, Width: null, Height: null, DurationSeconds: null);
    }

    private static List<NormalizedReaction> Reactions(string platform, JsonElement message)
    {
        var reactions = new List<NormalizedReaction>();

        if (!message.TryGetProperty("reactions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return reactions;
        }

        foreach (var reaction in list.EnumerateArray())
        {
            if (Text(reaction, "reaction") is not { } emoji)
            {
                continue;
            }

            var actor = Text(reaction, "actor") is { } name ? Person(platform, name) : null;

            reactions.Add(new NormalizedReaction(emoji, null, actor, 1, null));
        }

        return reactions;
    }

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    /// <summary>A string property, repaired.</summary>
    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text
            ? Repair(text)
            : null;
}
