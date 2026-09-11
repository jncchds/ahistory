using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archive.Import.GoogleChat;

namespace Archive.Import.Discord;

/// <summary>
/// Discord: its own data package, and DiscordChatExporter's JSON.
/// </summary>
/// <remarks>
/// <para>
/// Two readers under one platform, because they describe the same messages with the same ids —
/// Discord's snowflakes — and a message that turns up in both must land once.
/// </para>
/// <list type="bullet">
///   <item><b>The data package</b> ("Request all of my data"): <c>account/user.json</c> names the
///   account, <c>messages/index.json</c> names each channel, and every channel folder holds a
///   <c>channel.json</c> and its messages as <c>messages.csv</c> or, in newer packages,
///   <c>messages.json</c>. <b>It holds only the messages you sent.</b> Every conversation from it is
///   one-sided, and attachments are links, not files. That is what Discord gives, stated rather
///   than papered over.</item>
///   <item><b>DiscordChatExporter</b>: one JSON file per channel with everyone's messages, names,
///   replies and reactions — and the attachments themselves when it was run with
///   <c>--media</c>. It does not say whose account exported it, so the owner is inferred or asked
///   for.</item>
/// </list>
/// <para>
/// Traps: the CSV quotes messages that contain commas, quotes and newlines, and is read by a real
/// CSV reader rather than split on commas; the package's newer JSON writes timestamps with no zone,
/// which Discord generates in UTC; and channel types are numbers in one shape and names in the
/// other. An unknown type in either stops the import.
/// </para>
/// </remarks>
public sealed class DiscordImporter : IPlatformImporter
{
    public const string PlatformId = "discord";

    private const char Separator = (char)31;

    public string Platform => PlatformId;

    public string DisplayName => "Discord";

    public ImportDetection Detect(string path)
    {
        var package = PackageMessages(path);
        var exports = ExporterFiles(path);

        if (package is null && exports.Length == 0)
        {
            return ImportDetection.No;
        }

        var owner = package is null ? null : PackageOwner(package);
        var notes = new List<string>();

        if (package is not null)
        {
            notes.Add("Discord's own data package holds only the messages you sent — the other side of "
                + "every conversation is not in it, and attachments are links rather than files.");
        }

        if (exports.Length > 0)
        {
            notes.Add("DiscordChatExporter files hold everyone's messages; attachments are included only "
                + "when it was run with --media.");
        }

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: owner?.SourceIdentityId,
            AccountName: owner?.DisplayName,
            FileCount: (package is null ? 0 : ChannelFolders(package).Length) + exports.Length,
            Note: string.Join(" ", notes),
            AccountIdIsGuess: owner is null,
            AccountCandidates: owner is null ? Candidates(exports) : null);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var package = PackageMessages(path);
        var exports = ExporterFiles(path);

        if (package is null && exports.Length == 0)
        {
            throw new InvalidDataException(
                $"'{path}' holds neither a Discord data package nor DiscordChatExporter JSON.");
        }

        var documents = new List<(string File, JsonDocument Document)>();

        try
        {
            foreach (var file in exports)
            {
                documents.Add((file, Parse(file)));
            }

            var owner = (package is null ? null : PackageOwner(package))
                ?? StatedOwner(documents, options?.OwnerAccountId)
                ?? InferredOwner(documents);

            // The package is nothing but the owner's messages, so it needs someone to attribute
            // them to even when nobody said who: a placeholder, flagged as the guess it is.
            if (owner is null && package is not null)
            {
                owner = new NormalizedIdentity(
                    PlatformId, null, null, "You (account not identified)", IsSynthetic: true);
            }

            if (owner is not null)
            {
                sink.OnOwner(owner);
            }

            if (package is not null)
            {
                ReadPackage(package, owner!, sink);
            }

            foreach (var (file, document) in documents)
            {
                ReadExport(path, file, document.RootElement, owner, sink);
            }
        }
        finally
        {
            foreach (var (_, document) in documents)
            {
                document.Dispose();
            }
        }
    }

    // ---- The data package -------------------------------------------------------------------

    /// <summary>The package's <c>messages</c> folder: the one holding <c>index.json</c>.</summary>
    private static string? PackageMessages(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        foreach (var candidate in new[] { path, Path.Combine(path, "messages"), Path.Combine(path, "package", "messages") })
        {
            if (File.Exists(Path.Combine(candidate, "index.json")) && ChannelFolders(candidate).Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] ChannelFolders(string messages) =>
        [.. Directory.EnumerateDirectories(messages)
            .Where(d => File.Exists(Path.Combine(d, "channel.json")))
            .Order(StringComparer.Ordinal)];

    /// <summary>The account, from <c>account/user.json</c> beside the <c>messages</c> folder.</summary>
    private static NormalizedIdentity? PackageOwner(string messages)
    {
        var file = Path.Combine(Path.GetDirectoryName(messages) ?? messages, "account", "user.json");

        if (!File.Exists(file))
        {
            return null;
        }

        using var document = Parse(file);
        var user = document.RootElement;

        var id = Text(user, "id") ?? throw new InvalidDataException($"'{file}' has no id.");

        return Person(id, Text(user, "username"), Text(user, "global_name"));
    }

    private static void ReadPackage(string messages, NormalizedIdentity owner, IImportSink sink)
    {
        using var index = Parse(Path.Combine(messages, "index.json"));

        foreach (var folder in ChannelFolders(messages))
        {
            using var channel = Parse(Path.Combine(folder, "channel.json"));
            var root = channel.RootElement;

            var id = Text(root, "id") ?? throw new InvalidDataException($"'{folder}' has a channel.json with no id.");
            var type = Number(root, "type") ?? throw new InvalidDataException($"'{folder}' has a channel.json with no type.");

            var kind = type switch
            {
                1 => "dm",
                3 => "group",
                0 or 2 or 4 or 5 or 10 or 11 or 12 or 13 or 15 or 16 => "channel",
                _ => throw new InvalidDataException($"Channel {id} in '{folder}' has type {type}, which this reader does not know."),
            };

            var described = Text(index.RootElement, id);
            var recipients = Strings(root, "recipients");
            var otherName = DmName(described);

            var roster = recipients
                .Select(r => r == owner.SourceIdentityId
                    ? owner
                    : Person(r, kind == "dm" ? otherName : null, null))
                .ToArray();

            var title = kind switch
            {
                "dm" => otherName ?? described,
                "group" => Text(root, "name") ?? described,
                _ => root.TryGetProperty("guild", out var guild) && Text(guild, "name") is { } server
                    ? $"{server} #{Text(root, "name")}"
                    : described,
            };

            var thread = new NormalizedThread(id, kind, title, roster);
            sink.OnThread(thread);

            foreach (var row in PackageRows(folder).OrderBy(r => r.At))
            {
                var media = row.Attachments
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Linked)
                    .ToList();

                sink.OnMessage(thread, Message(id, row.Id, row.At, owner, row.Contents, media, edited: null, replyTo: null, reactions: [], service: null, raw: null));
            }
        }
    }

    private sealed record PackageRow(string Id, DateTimeOffset At, string Contents, string Attachments);

    private static IEnumerable<PackageRow> PackageRows(string folder)
    {
        var json = Path.Combine(folder, "messages.json");

        if (File.Exists(json))
        {
            using var document = Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"'{json}' is not an array of messages.");
            }

            var rows = new List<PackageRow>();

            foreach (var row in document.RootElement.EnumerateArray())
            {
                var id = Text(row, "ID") ?? throw new InvalidDataException($"A message in '{json}' has no ID.");
                var at = PackageTime(Text(row, "Timestamp"), json);

                rows.Add(new PackageRow(id, at, Text(row, "Contents") ?? string.Empty, Text(row, "Attachments") ?? string.Empty));
            }

            return rows;
        }

        var csv = Path.Combine(folder, "messages.csv");

        if (!File.Exists(csv))
        {
            return [];
        }

        using var reader = new StreamReader(csv, Encoding.UTF8);
        var records = Csv(reader).ToList();

        if (records.Count == 0)
        {
            return [];
        }

        var header = records[0];

        if (header.Length < 4 ||
            !header[0].Trim((char)0xFEFF).Equals("ID", StringComparison.OrdinalIgnoreCase) ||
            !header[1].Equals("Timestamp", StringComparison.OrdinalIgnoreCase) ||
            !header[2].Equals("Contents", StringComparison.OrdinalIgnoreCase) ||
            !header[3].Equals("Attachments", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{csv}' does not start with ID,Timestamp,Contents,Attachments.");
        }

        return [.. records.Skip(1).Select(r => r.Length < 4
            ? throw new InvalidDataException($"A row in '{csv}' has {r.Length} fields, not 4.")
            : new PackageRow(r[0], PackageTime(r[1], csv), r[2], r[3]))];
    }

    /// <summary>
    /// A package timestamp: <c>2021-03-14 22:41:03.123000+00:00</c> in the CSV, and in newer
    /// packages the same without its zone, which Discord generates in UTC.
    /// </summary>
    private static DateTimeOffset PackageTime(string? value, string file) =>
        value is not null &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime()
            : throw new InvalidDataException($"'{value}' in '{file}' is not a time.");

    /// <summary><c>Direct Message with sam#0</c> → <c>sam</c>; nothing for a participant Discord could not name.</summary>
    private static string? DmName(string? described)
    {
        const string Prefix = "Direct Message with ";

        if (described is null || !described.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = described[Prefix.Length..];
        var hash = name.LastIndexOf('#');

        if (hash > 0 && name[(hash + 1)..].All(char.IsAsciiDigit))
        {
            name = name[..hash];
        }

        return name == "Unknown Participant" ? null : name;
    }

    /// <summary>
    /// RFC 4180: fields quoted when they hold commas, quotes or newlines, quotes doubled inside.
    /// </summary>
    internal static IEnumerable<string[]> Csv(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        int next;

        while ((next = reader.Read()) != -1)
        {
            var c = (char)next;

            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else if (c != '\r')
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;

                case '\r':
                    break;

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return [.. fields];
                    fields.Clear();
                    any = false;
                    break;

                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("A CSV field opens a quote it never closes.");
        }

        if (any)
        {
            fields.Add(field.ToString());
            yield return [.. fields];
        }
    }

    // ---- DiscordChatExporter ----------------------------------------------------------------

    /// <summary>JSON files at the folder's root that open with a guild, a channel and messages.</summary>
    private static string[] ExporterFiles(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => Head(f) is var head
                && head.Contains("\"guild\"", StringComparison.Ordinal)
                && head.Contains("\"channel\"", StringComparison.Ordinal)
                && head.Contains("\"messages\"", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    private static string Head(string file)
    {
        using var stream = File.OpenRead(file);
        var buffer = new byte[4096];
        var read = stream.Read(buffer);

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static readonly Dictionary<string, string?> MessageTypes = new(StringComparer.Ordinal)
    {
        ["Default"] = null,
        ["Reply"] = null,
        ["ChatInputCommand"] = null,
        ["ContextMenuCommand"] = null,
        ["ThreadStarterMessage"] = null,
        ["Call"] = "phone_call",
        ["RecipientAdd"] = "invite_members",
        ["RecipientRemove"] = "remove_members",
        ["ChannelNameChange"] = "edit_group_title",
        ["ChannelIconChange"] = "edit_group_photo",
        ["ChannelPinnedMessage"] = "pin_message",
        ["GuildMemberJoin"] = "join_group_by_link",
        ["ThreadCreated"] = "thread_created",
    };

    private void ReadExport(string exportRoot, string file, JsonElement root, NormalizedIdentity? owner, IImportSink sink)
    {
        if (!root.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"'{file}' has no channel.");
        }

        var id = Text(channel, "id") ?? throw new InvalidDataException($"'{file}' has a channel with no id.");
        var type = Text(channel, "type");
        var name = Text(channel, "name");

        var kind = type switch
        {
            "DirectTextChat" => "dm",
            "DirectGroupTextChat" => "group",
            "GuildTextChat" or "GuildNewsChat" or "GuildAnnouncement" or "GuildVoiceChat" or "GuildStageVoice"
                or "GuildPublicThread" or "GuildPrivateThread" or "GuildNewsThread" or "GuildForum" or "GuildCategory" => "channel",
            _ => throw new InvalidDataException($"'{file}' has a channel of type '{type}', which this reader does not know."),
        };

        var title = kind == "channel" && root.TryGetProperty("guild", out var guild) && Text(guild, "name") is { } server
            ? $"{server} #{name}"
            : name;

        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"'{file}' has no messages array.");
        }

        var roster = messages.EnumerateArray()
            .Select(m => m.TryGetProperty("author", out var a) ? Author(a, owner) : null)
            .OfType<NormalizedIdentity>()
            .DistinctBy(p => p.SourceIdentityId)
            .ToArray();

        var thread = new NormalizedThread(id, kind, title, roster);
        sink.OnThread(thread);

        var folder = Path.GetDirectoryName(file)!;

        foreach (var message in messages.EnumerateArray())
        {
            var messageId = Text(message, "id") ?? throw new InvalidDataException($"A message in '{file}' has no id.");
            var messageType = Text(message, "type") ?? "Default";

            if (!MessageTypes.TryGetValue(messageType, out var service))
            {
                throw new InvalidDataException($"Message {messageId} in '{file}' has type '{messageType}', which this reader does not know.");
            }

            var at = ExporterTime(Text(message, "timestamp"), file);
            var edited = Text(message, "timestampEdited") is { } e ? ExporterTime(e, file).ToString("O", CultureInfo.InvariantCulture) : null;

            var author = message.TryGetProperty("author", out var a) ? Author(a, owner) : null;

            var media = new List<NormalizedMedia>();

            if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
            {
                foreach (var attachment in attachments.EnumerateArray())
                {
                    media.Add(ExporterAttachment(exportRoot, folder, attachment));
                }
            }

            if (message.TryGetProperty("stickers", out var stickers) && stickers.ValueKind == JsonValueKind.Array)
            {
                foreach (var sticker in stickers.EnumerateArray())
                {
                    media.Add(new NormalizedMedia(
                        Text(sticker, "sourceUrl") ?? Text(sticker, "name") ?? "sticker", "sticker", Text(sticker, "name"), Mime: null,
                        MissingReason: "Discord stickers are linked rather than exported",
                        StickerEmoji: null, Width: null, Height: null, DurationSeconds: null));
                }
            }

            string? replyTo = null;

            if (message.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.Object &&
                Text(reference, "messageId") is { } referenced)
            {
                replyTo = $"dc/{Text(reference, "channelId") ?? id}/{referenced}";
            }

            sink.OnMessage(thread, Message(
                id, messageId, at, author, Text(message, "content") ?? string.Empty, media, edited, replyTo,
                Reactions(message, owner), service, message.GetRawText()));
        }
    }

    private static DateTimeOffset ExporterTime(string? value, string file) =>
        value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime()
            : throw new InvalidDataException($"'{value}' in '{file}' is not a time.");

    private static NormalizedIdentity? Author(JsonElement author, NormalizedIdentity? owner)
    {
        var id = Text(author, "id");

        if (id is null)
        {
            return null;
        }

        return owner is not null && owner.SourceIdentityId == id
            ? owner
            : Person(id, Text(author, "name"), Text(author, "nickname"));
    }

    /// <summary>
    /// An attachment: a file beside the export when it was run with <c>--media</c>, a link otherwise.
    /// </summary>
    private static NormalizedMedia ExporterAttachment(string exportRoot, string folder, JsonElement attachment)
    {
        var url = Text(attachment, "url") ?? string.Empty;
        var name = Text(attachment, "fileName");

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return Linked(url) with { OriginalFilename = name ?? Path.GetFileName(absolute.AbsolutePath) };
        }

        var relative = Path.GetRelativePath(exportRoot, Path.Combine(folder, Uri.UnescapeDataString(url))).Replace('\\', '/');

        return new NormalizedMedia(
            relative, GoogleChatImporter.MediaKindOf(name ?? url), name, Mime: null, MissingReason: null,
            StickerEmoji: null, Width: null, Height: null, DurationSeconds: null);
    }

    /// <summary>
    /// Reactions: one row per named reactor, and one unnamed row for the rest of the count.
    /// </summary>
    private static List<NormalizedReaction> Reactions(JsonElement message, NormalizedIdentity? owner)
    {
        var reactions = new List<NormalizedReaction>();

        if (!message.TryGetProperty("reactions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return reactions;
        }

        foreach (var reaction in list.EnumerateArray())
        {
            if (!reaction.TryGetProperty("emoji", out var emoji) || Text(emoji, "name") is not { } symbol)
            {
                continue;
            }

            var custom = Text(emoji, "id") is { Length: > 0 } customId ? customId : null;
            var count = Number(reaction, "count") ?? 1;
            var named = 0;

            if (reaction.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            {
                foreach (var user in users.EnumerateArray())
                {
                    if (Author(user, owner) is { } actor)
                    {
                        reactions.Add(new NormalizedReaction(symbol, custom, actor, 1, null));
                        named++;
                    }
                }
            }

            if (count > named)
            {
                reactions.Add(new NormalizedReaction(symbol, custom, null, count - named, null));
            }
        }

        return reactions;
    }

    // ---- Owners -----------------------------------------------------------------------------

    /// <summary>The user's answer, matched against the authors by id, name or nickname.</summary>
    private static NormalizedIdentity? StatedOwner(List<(string File, JsonDocument Document)> documents, string? stated)
    {
        if (string.IsNullOrWhiteSpace(stated))
        {
            return null;
        }

        var answer = stated.Trim();

        foreach (var author in Authors(documents))
        {
            if (author.SourceIdentityId == answer ||
                string.Equals(author.Handle, answer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(author.DisplayName, answer, StringComparison.OrdinalIgnoreCase))
            {
                return author;
            }
        }

        return answer.All(char.IsAsciiDigit)
            ? Person(answer, null, null)
            : new NormalizedIdentity(PlatformId, null, null, answer, IsSynthetic: true);
    }

    /// <summary>
    /// The one author in every export, when there are two or more; in a single direct chat, the
    /// author who is not the person the channel is named after.
    /// </summary>
    private static NormalizedIdentity? InferredOwner(List<(string File, JsonDocument Document)> documents)
    {
        if (documents.Count == 0)
        {
            return null;
        }

        var perFile = documents
            .Select(d => AuthorsOf(d.Document.RootElement).ToDictionary(a => a.SourceIdentityId!, StringComparer.Ordinal))
            .ToArray();

        if (perFile.Length >= 2)
        {
            var common = perFile[0].Keys.ToHashSet(StringComparer.Ordinal);

            foreach (var file in perFile.Skip(1))
            {
                common.IntersectWith(file.Keys);
            }

            return common.Count == 1 ? perFile[0][common.First()] : null;
        }

        var root = documents[0].Document.RootElement;

        if (!root.TryGetProperty("channel", out var channel) || Text(channel, "type") != "DirectTextChat")
        {
            return null;
        }

        var name = Text(channel, "name");
        var others = perFile[0].Values.Where(a => a.Handle != name && a.DisplayName != name).ToArray();

        return others.Length == 1 ? others[0] : null;
    }

    private static IEnumerable<NormalizedIdentity> Authors(List<(string File, JsonDocument Document)> documents) =>
        documents.SelectMany(d => AuthorsOf(d.Document.RootElement)).DistinctBy(a => a.SourceIdentityId);

    private static IEnumerable<NormalizedIdentity> AuthorsOf(JsonElement root) =>
        root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            ? messages.EnumerateArray()
                .Select(m => m.TryGetProperty("author", out var a) ? Author(a, null) : null)
                .OfType<NormalizedIdentity>()
                .DistinctBy(a => a.SourceIdentityId)
            : [];

    /// <summary>Authors across the exports, those in the most files first.</summary>
    private static IReadOnlyList<string> Candidates(string[] exports)
    {
        var counts = new Dictionary<string, (int Files, string Name)>(StringComparer.Ordinal);

        foreach (var file in exports.Take(20))
        {
            using var document = Parse(file);

            foreach (var author in AuthorsOf(document.RootElement))
            {
                var existing = counts.GetValueOrDefault(author.SourceIdentityId!);
                counts[author.SourceIdentityId!] = (existing.Files + 1, author.Handle ?? author.DisplayName);
            }
        }

        return [.. counts.Values.OrderByDescending(v => v.Files).ThenBy(v => v.Name, StringComparer.Ordinal).Take(5).Select(v => v.Name)];
    }

    // ---- Shared -----------------------------------------------------------------------------

    private static NormalizedIdentity Person(string id, string? username, string? displayName) =>
        new(PlatformId, id, username, displayName ?? username ?? id, IsSynthetic: false);

    private static NormalizedMedia Linked(string url) =>
        new(url, GoogleChatImporter.MediaKindOf(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url),
            OriginalFilename: Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? Path.GetFileName(parsed.AbsolutePath) : null,
            Mime: null,
            MissingReason: "Discord linked this attachment rather than including it",
            StickerEmoji: null, Width: null, Height: null, DurationSeconds: null);

    /// <summary>
    /// One message, the same way from either shape, so a message present in both hashes alike.
    /// </summary>
    private static NormalizedMessage Message(
        string channelId, string messageId, DateTimeOffset at, NormalizedIdentity? sender, string text,
        List<NormalizedMedia> media, string? edited, string? replyTo, List<NormalizedReaction> reactions,
        string? service, string? raw) => new()
    {
        Uid = $"dc/{channelId}/{messageId}",
        SourceThreadId = channelId,
        Kind = service is null ? "message" : "service",
        Sender = sender,
        ServiceAction = service,
        SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
        SentAtUnix = at.ToUnixTimeSeconds(),
        Plaintext = text,
        ContentHash = Hash($"{text}{Separator}{string.Join(Separator, media.Select(m => m.OriginalFilename ?? m.ExportPath))}"),
        ReplyToUid = replyTo,
        EditedAtUtc = edited,
        RawJson = raw,
        Media = media,
        Reactions = reactions,
    };

    private static JsonDocument Parse(string file) =>
        JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true });

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var n)
            ? n
            : null;

    private static string[] Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()).OfType<string>()]
            : [];
}
