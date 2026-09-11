using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Archive.Import.GoogleChat;

/// <summary>
/// Google Chat, as Takeout exports it.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Google Chat</c> folder with two halves. <c>Users/User &lt;id&gt;/user_info.json</c> names
/// the account the export belongs to — stated, not inferred. <c>Groups/&lt;DM or Space
/// id&gt;/</c> holds one conversation each: <c>group_info.json</c> with its members and, for a
/// space, its name; <c>messages.json</c> with the messages; and the attachments themselves, beside
/// them under the name <c>export_name</c> gives.
/// </para>
/// <para>
/// Traps this format has:
/// </para>
/// <list type="bullet">
///   <item><b>Dates are English prose</b> — <c>Tuesday, March 14, 2021 at 10:41:03 PM UTC</c> in
///   one export, <c>Tuesday, 14 March 2021 at 22:41:03 UTC</c> in another, sometimes with a narrow
///   no-break space before the AM. The month names are mapped explicitly, because this app runs
///   with invariant globalization, and anything that is not one of these shapes is refused rather
///   than parsed by a culture that might read it differently.</item>
///   <item><b>People are email addresses.</b> That is the stable key, and a strong one: the same
///   address in a Hangouts roster is the same person. A creator with no email — a deleted account —
///   is keyed by name and flagged, like every name-only sender.</item>
///   <item><b>A DM has no name.</b> It is titled after the other member, found by comparing
///   addresses with the owner's, never by taking the first member listed.</item>
///   <item><b>Older exports have no <c>message_id</c>.</b> The uid is then derived from the time,
///   the sender and the text, with an occurrence count for repeats — stable across re-exports,
///   at the price that an edited message in such an export arrives as a new one.</item>
/// </list>
/// <para>
/// The sibling of <see cref="Hangouts.HangoutsImporter"/> in the same Takeout; a Takeout root holding
/// both is detected as both, and each is imported on its own.
/// </para>
/// </remarks>
public sealed partial class GoogleChatImporter : IPlatformImporter
{
    public const string PlatformId = "googlechat";

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["January"] = 1, ["February"] = 2, ["March"] = 3, ["April"] = 4, ["May"] = 5, ["June"] = 6,
        ["July"] = 7, ["August"] = 8, ["September"] = 9, ["October"] = 10, ["November"] = 11, ["December"] = 12,
    };

    /// <summary><c>[Weekday, ]March 14, 2021 at 10:41:03 PM UTC</c>.</summary>
    [GeneratedRegex(
        @"^(?:[A-Za-z]+,\s+)?(?<month>[A-Za-z]+)\s+(?<day>\d{1,2}),\s+(?<year>\d{4})\s+at\s+(?<time>\d{1,2}:\d{2}(?::\d{2})?)(?:\s*(?<ampm>AM|PM))?\s+(?<zone>UTC|GMT)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MonthFirst();

    /// <summary><c>[Weekday, ]14 March 2021 at 22:41:03 UTC</c>.</summary>
    [GeneratedRegex(
        @"^(?:[A-Za-z]+,\s+)?(?<day>\d{1,2})\s+(?<month>[A-Za-z]+)\s+(?<year>\d{4})\s+at\s+(?<time>\d{1,2}:\d{2}(?::\d{2})?)(?:\s*(?<ampm>AM|PM))?\s+(?<zone>UTC|GMT)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayFirst();

    public string Platform => PlatformId;

    public string DisplayName => "Google Chat";

    public ImportDetection Detect(string path)
    {
        var root = ChatRoot(path);

        if (root is null)
        {
            return ImportDetection.No;
        }

        var files = MessageFiles(root);

        if (files.Length == 0 || !Mentions(files[0], "\"messages\""))
        {
            return ImportDetection.No;
        }

        var owner = ReadOwner(root);

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: owner?.SourceIdentityId,
            AccountName: owner?.DisplayName,
            FileCount: files.Length,
            Note: owner is null
                ? "This export has no Users folder, so it does not say whose it is. Keep the whole "
                  + "Google Chat folder from Takeout together."
                : null);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var root = ChatRoot(path)
            ?? throw new InvalidDataException(
                $"'{path}' has no Google Chat folder. Take Google Chat from Google Takeout and point at it or its parent.");

        var owner = ReadOwner(root);

        if (owner is not null)
        {
            sink.OnOwner(owner);
        }

        foreach (var group in Directory.EnumerateDirectories(Path.Combine(root, "Groups"))
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            ReadGroup(path, group, owner, sink);
        }
    }

    /// <summary>The <c>Google Chat</c> folder, pointed at directly, via its parent, or via a Takeout root.</summary>
    private static string? ChatRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     path,
                     Path.Combine(path, "Google Chat"),
                     Path.Combine(path, "Takeout", "Google Chat"),
                 })
        {
            if (Directory.Exists(Path.Combine(candidate, "Groups")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] MessageFiles(string root) =>
        [.. Directory.EnumerateDirectories(Path.Combine(root, "Groups"))
            .Select(g => Path.Combine(g, "messages.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)];

    private static bool Mentions(string file, string text)
    {
        using var stream = File.OpenRead(file);
        var buffer = new byte[4096];
        var read = stream.Read(buffer);

        return Encoding.UTF8.GetString(buffer, 0, read).Contains(text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The account the export belongs to, from <c>Users/User &lt;id&gt;/user_info.json</c>.
    /// </summary>
    /// <remarks>
    /// A Takeout is one account's, so there is one such folder. More than one would mean folders
    /// from two exports merged by hand, and picking either would attribute the other's messages
    /// to the wrong side of every conversation — so that is refused.
    /// </remarks>
    private static NormalizedIdentity? ReadOwner(string root)
    {
        var users = Path.Combine(root, "Users");

        if (!Directory.Exists(users))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(users, "user_info.json", SearchOption.AllDirectories).ToArray();

        if (files.Length == 0)
        {
            return null;
        }

        if (files.Length > 1)
        {
            throw new InvalidDataException(
                $"'{users}' holds {files.Length} accounts. A Takeout belongs to one; import each export from its own folder.");
        }

        using var document = Parse(files[0]);

        if (!document.RootElement.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"'{files[0]}' has no user block. This is not the layout this reader knows.");
        }

        return Person(user)
            ?? throw new InvalidDataException($"'{files[0]}' names no account.");
    }

    private void ReadGroup(string exportRoot, string group, NormalizedIdentity? owner, IImportSink sink)
    {
        var messagesFile = Path.Combine(group, "messages.json");

        if (!File.Exists(messagesFile))
        {
            // A group whose info survived and whose messages did not: nothing to read, and the
            // info alone is a conversation that never happened as far as the archive can tell.
            return;
        }

        var folderName = Path.GetFileName(group);
        var (kindPrefix, groupId) = SplitFolder(folderName);

        var members = new List<NormalizedIdentity>();
        string? spaceName = null;

        var infoFile = Path.Combine(group, "group_info.json");

        if (File.Exists(infoFile))
        {
            using var info = Parse(infoFile);

            spaceName = Text(info.RootElement, "name");

            if (info.RootElement.TryGetProperty("members", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                members.AddRange(list.EnumerateArray().Select(Person).OfType<NormalizedIdentity>());
            }
        }

        var kind = kindPrefix switch
        {
            "DM" => "dm",
            "Space" => "group",
            _ when members.Count <= 2 => "dm",
            _ => "group",
        };

        var sourceThreadId = $"{kindPrefix.ToLowerInvariant()}/{groupId}";

        var title = kind == "dm"
            ? members.FirstOrDefault(m => owner is null || m.SourceIdentityId != owner.SourceIdentityId)?.DisplayName
              ?? spaceName
            : spaceName;

        var thread = new NormalizedThread(sourceThreadId, kind, title, members);
        sink.OnThread(thread);

        var names = members
            .Where(m => m.SourceIdentityId is not null)
            .GroupBy(m => m.SourceIdentityId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        using var document = Parse(messagesFile);

        if (!document.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"'{messagesFile}' has no messages array.");
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var message in messages.EnumerateArray())
        {
            sink.OnMessage(thread, ReadMessage(exportRoot, group, messagesFile, sourceThreadId, message, names, seen));
        }
    }

    /// <summary><c>DM abc123</c> or <c>Space xyz</c>: the kind, and the group's own id.</summary>
    private static (string Kind, string Id) SplitFolder(string folderName)
    {
        var space = folderName.IndexOf(' ', StringComparison.Ordinal);

        return space > 0
            ? (folderName[..space], folderName[(space + 1)..])
            : ("Group", folderName);
    }

    private NormalizedMessage ReadMessage(
        string exportRoot,
        string group,
        string file,
        string sourceThreadId,
        JsonElement message,
        Dictionary<string, NormalizedIdentity> members,
        Dictionary<string, int> seen)
    {
        var created = Text(message, "created_date")
            ?? throw new InvalidDataException($"A message in '{file}' has no created_date.");

        var at = ParseDate(created, file);

        if (!message.TryGetProperty("creator", out var creator) || Person(creator) is not { } sender)
        {
            throw new InvalidDataException($"A message in '{file}' has no creator.");
        }

        // The roster's copy where there is one: it carries the name the conversation used.
        if (sender.SourceIdentityId is { } email && members.TryGetValue(email, out var known))
        {
            sender = known;
        }

        var text = Text(message, "text") ?? string.Empty;
        var media = Attachments(exportRoot, group, message);
        var entities = message.TryGetProperty("annotations", out var annotations) &&
                       annotations.ValueKind == JsonValueKind.Array && annotations.GetArrayLength() > 0
            ? annotations.GetRawText()
            : null;

        var uid = Text(message, "message_id") is { } id
            ? $"gc/{id}"
            : DerivedUid(sourceThreadId, at, sender, text, seen);

        string? replyTo = null;

        if (message.TryGetProperty("quoted_message_metadata", out var quoted) &&
            quoted.ValueKind == JsonValueKind.Object &&
            Text(quoted, "message_id") is { } quotedId)
        {
            replyTo = $"gc/{quotedId}";
        }

        return new NormalizedMessage
        {
            Uid = uid,
            SourceThreadId = sourceThreadId,
            Kind = "message",
            Sender = sender,
            SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
            SentAtUnix = at.ToUnixTimeSeconds(),
            Plaintext = text,
            EntitiesJson = entities,
            ContentHash = ContentHash(text, entities, media),
            ReplyToUid = replyTo,
            EditedAtUtc = Text(message, "updated_date") is { } updated
                ? ParseDate(updated, file).ToString("O", CultureInfo.InvariantCulture)
                : null,
            RawJson = message.GetRawText(),
            Media = media,
            Reactions = Reactions(message, members),
        };
    }

    /// <summary>
    /// A uid for an export too old to carry <c>message_id</c>.
    /// </summary>
    /// <remarks>
    /// Keyed on what the message is rather than where it sits, so deleting an earlier message
    /// before re-exporting does not shift every later uid. Two identical messages in the same
    /// second are told apart by an occurrence count.
    /// </remarks>
    private static string DerivedUid(
        string sourceThreadId, DateTimeOffset at, NormalizedIdentity sender, string text, Dictionary<string, int> seen)
    {
        var key = Hash($"{at.ToUnixTimeSeconds()}{Separator}{sender.SourceIdentityId ?? sender.DisplayName}{Separator}{text}")[..16];
        var occurrence = seen.GetValueOrDefault(key);
        seen[key] = occurrence + 1;

        return $"gc/{sourceThreadId}/{key}/{occurrence}";
    }

    /// <summary>
    /// Takeout's English date prose, in either order, with the zone stated.
    /// </summary>
    internal static DateTimeOffset ParseDate(string value, string file)
    {
        // Takeout puts U+202F before AM/PM in newer exports, and a plain space in older ones.
        var text = value.Replace((char)0x202F, ' ').Replace((char)0x00A0, ' ').Trim();

        var match = MonthFirst().Match(text);

        if (!match.Success)
        {
            match = DayFirst().Match(text);
        }

        if (!match.Success)
        {
            if (text.Contains('T', StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            {
                return iso.ToUniversalTime();
            }

            throw new InvalidDataException(
                $"'{value}' in '{file}' is not a date this reader knows. Google Chat dates are read in "
                + "English only; export Takeout with the account language set to English.");
        }

        if (!Months.TryGetValue(match.Groups["month"].Value, out var month))
        {
            throw new InvalidDataException($"'{match.Groups["month"].Value}' in '{file}' is not an English month name.");
        }

        var time = match.Groups["time"].Value.Split(':');
        var hour = int.Parse(time[0], CultureInfo.InvariantCulture);
        var minute = int.Parse(time[1], CultureInfo.InvariantCulture);
        var second = time.Length > 2 ? int.Parse(time[2], CultureInfo.InvariantCulture) : 0;

        if (match.Groups["ampm"].Success)
        {
            if (hour is < 1 or > 12)
            {
                throw new InvalidDataException($"'{value}' in '{file}' has a 12-hour time outside 1-12.");
            }

            var pm = match.Groups["ampm"].Value.Equals("PM", StringComparison.OrdinalIgnoreCase);
            hour = (hour % 12) + (pm ? 12 : 0);
        }

        return new DateTimeOffset(
            int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
            month,
            int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture),
            hour, minute, second, TimeSpan.Zero);
    }

    /// <summary>
    /// Attachments, which Takeout puts in the conversation's own folder under <c>export_name</c>.
    /// </summary>
    private static List<NormalizedMedia> Attachments(string exportRoot, string group, JsonElement message)
    {
        var media = new List<NormalizedMedia>();

        if (!message.TryGetProperty("attached_files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return media;
        }

        foreach (var file in files.EnumerateArray())
        {
            var exportName = Text(file, "export_name");
            var original = Text(file, "original_name");

            if (exportName is null)
            {
                media.Add(new NormalizedMedia(
                    original ?? "attachment", MediaKindOf(original), original, Mime: null,
                    MissingReason: "Google Chat did not export this attachment",
                    StickerEmoji: null, Width: null, Height: null, DurationSeconds: null));

                continue;
            }

            var relative = Path.GetRelativePath(exportRoot, Path.Combine(group, exportName)).Replace('\\', '/');

            media.Add(new NormalizedMedia(
                relative, MediaKindOf(original ?? exportName), original, Mime: null, MissingReason: null,
                StickerEmoji: null, Width: null, Height: null, DurationSeconds: null));
        }

        return media;
    }

    internal static string MediaKindOf(string? filename) =>
        Path.GetExtension(filename ?? string.Empty).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" or ".bmp" => "photo",
            ".gif" => "animation",
            ".mp4" or ".mov" or ".webm" or ".mkv" or ".3gp" => "video",
            ".mp3" or ".m4a" or ".wav" or ".flac" => "audio",
            ".ogg" or ".opus" or ".amr" => "voice",
            _ => "file",
        };

    /// <summary>One row per reactor, named from the roster where the roster knows them.</summary>
    private static List<NormalizedReaction> Reactions(JsonElement message, Dictionary<string, NormalizedIdentity> members)
    {
        var reactions = new List<NormalizedReaction>();

        if (!message.TryGetProperty("reactions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return reactions;
        }

        foreach (var reaction in list.EnumerateArray())
        {
            string? emoji = null;
            string? custom = null;

            if (reaction.TryGetProperty("emoji", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                emoji = Text(e, "unicode");

                if (emoji is null && e.TryGetProperty("custom_emoji", out var c) && c.ValueKind == JsonValueKind.Object)
                {
                    custom = Text(c, "uid");
                    emoji = Text(c, "short_code") ?? "custom";
                }
            }

            if (emoji is null)
            {
                continue;
            }

            if (!reaction.TryGetProperty("reactor_emails", out var reactors) || reactors.ValueKind != JsonValueKind.Array ||
                reactors.GetArrayLength() == 0)
            {
                reactions.Add(new NormalizedReaction(emoji, custom, null, 1, null));
                continue;
            }

            foreach (var reactor in reactors.EnumerateArray())
            {
                if (reactor.GetString() is not { } address)
                {
                    continue;
                }

                var key = address.ToLowerInvariant();
                var actor = members.GetValueOrDefault(key)
                    ?? new NormalizedIdentity(PlatformId, key, address, address, IsSynthetic: false);

                reactions.Add(new NormalizedReaction(emoji, custom, actor, 1, null));
            }
        }

        return reactions;
    }

    /// <summary>A person as Google Chat writes one: a name, and usually an email address.</summary>
    private static NormalizedIdentity? Person(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = Text(element, "name");
        var email = Text(element, "email");

        if (!string.IsNullOrWhiteSpace(email))
        {
            // Lower-cased because the domain part is case-insensitive and Takeout is not
            // consistent about it; the address as written is kept as the handle.
            return new NormalizedIdentity(
                PlatformId, email.ToLowerInvariant(), email, string.IsNullOrWhiteSpace(name) ? email : name, IsSynthetic: false);
        }

        return string.IsNullOrWhiteSpace(name)
            ? null
            : new NormalizedIdentity(PlatformId, null, null, name, IsSynthetic: true);
    }

    private static JsonDocument Parse(string file) =>
        JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true });

    private static string ContentHash(string text, string? entities, List<NormalizedMedia> media)
    {
        var builder = new StringBuilder().Append(text).Append(Separator).Append(entities).Append(Separator);

        foreach (var item in media)
        {
            builder.Append(item.ExportPath).Append(Separator);
        }

        return Hash(builder.ToString());
    }

    private const char Separator = (char)31;

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
