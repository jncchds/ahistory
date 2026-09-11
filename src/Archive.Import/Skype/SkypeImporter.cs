using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Archive.Import.Skype;

/// <summary>
/// Skype, from Microsoft's "Export files and chat history".
/// </summary>
/// <remarks>
/// <para>
/// A <c>.tar</c> holding <c>messages.json</c>: the account's <c>userId</c>, then a
/// <c>conversations</c> array, each with its <c>MessageList</c>. Unpack the tar and point at the
/// folder. Every message carries a server id, so this is one of the strong formats: re-exports land
/// on the same rows, and an edit is a revision rather than a second message.
/// </para>
/// <para>
/// Traps:
/// </para>
/// <list type="bullet">
///   <item><b>Content is markup</b>, not text: <c>&lt;b&gt;</c>, links, emoticons as
///   <c>&lt;ss type="smile"&gt;(smile)&lt;/ss&gt;</c>, quotes carrying a <c>legacyquote</c> copy of
///   their own header, an <c>&lt;e_m/&gt;</c> marker on edited messages, and shared files as
///   <c>&lt;URIObject&gt;</c>. It is read as XML and walked, keeping what a person would have
///   seen; content that is not well-formed falls back to stripping tags.</item>
///   <item><b>Conversation ids say what they are</b>: <c>8:</c> a person, <c>19:</c> a group,
///   <c>28:</c> a bot, <c>4:</c> a phone number, <c>48:</c> Skype's own feeds — call logs and
///   notifications, whose calls already appear in the conversations they belong to. Any other
///   prefix stops the import.</item>
///   <item><b>Senders were URLs</b> in older exports —
///   <c>https://…/v1/users/ME/contacts/8:live:sam</c> — and are bare ids in newer ones. Both are
///   read down to the id, so two exports of one account agree on who everyone is.</item>
///   <item><b>Files are linked, not included.</b> A shared photo is recorded, with its name, as an
///   attachment the export did not carry.</item>
/// </list>
/// </remarks>
public sealed partial class SkypeImporter : IPlatformImporter
{
    public const string PlatformId = "skype";

    /// <summary>Message types that are Skype talking to itself, skipped by name rather than silently.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "Notice",
        "PopCard",
        "InviteFreeRelationshipChanged/Initialized",
    };

    [GeneratedRegex(@"""userId""\s*:\s*""(?<id>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdPattern();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    public string Platform => PlatformId;

    public string DisplayName => "Skype";

    public ImportDetection Detect(string path)
    {
        var file = FindExport(path);

        if (file is null)
        {
            return ImportDetection.No;
        }

        var head = Head(file);
        var owner = UserIdPattern().Match(head) is { Success: true } match ? Mri(match.Groups["id"].Value) : null;

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: owner,
            AccountName: owner,
            FileCount: 1,
            Note: "Skype's export links to shared files rather than including them, so photos and "
                + "files are recorded by name without their contents.");
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var file = FindExport(path)
            ?? throw new InvalidDataException(
                $"'{path}' has no Skype messages.json. Unpack the .tar Skype's export downloads and point at that folder.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = document.RootElement;

        var userId = Text(root, "userId") is { } id ? Mri(id) : null;
        NormalizedIdentity? owner = userId is null ? null : Person(userId, null);

        if (owner is not null)
        {
            sink.OnOwner(owner);
        }

        if (!root.TryGetProperty("conversations", out var conversations) || conversations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"'{file}' has no conversations array.");
        }

        foreach (var conversation in conversations.EnumerateArray())
        {
            ReadConversation(file, conversation, owner, sink);
        }
    }

    /// <summary><c>messages.json</c> at the folder's root or one level down, holding a Skype export.</summary>
    private static string? FindExport(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        return Directory.EnumerateFiles(path, "messages.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(path).SelectMany(d => Directory.EnumerateFiles(d, "messages.json")))
            .FirstOrDefault(f => Head(f) is var head
                && head.Contains("\"userId\"", StringComparison.Ordinal)
                && head.Contains("\"conversations\"", StringComparison.Ordinal));
    }

    private static string Head(string file)
    {
        using var stream = File.OpenRead(file);
        var buffer = new byte[4096];
        var read = stream.Read(buffer);

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>
    /// An id with any URL in front of it removed.
    /// </summary>
    /// <remarks>
    /// Older exports wrote <c>https://…/contacts/8:live:sam</c>; newer ones write <c>8:live:sam</c>.
    /// </remarks>
    internal static string Mri(string value)
    {
        var slash = value.LastIndexOf('/');

        return slash >= 0 ? value[(slash + 1)..] : value;
    }

    private static NormalizedIdentity Person(string mri, string? name) =>
        new(PlatformId, mri, Handle: Handle(mri), string.IsNullOrWhiteSpace(name) ? Handle(mri) : name, IsSynthetic: false);

    /// <summary>The Skype name a person would recognize: the id without its <c>8:</c>.</summary>
    private static string Handle(string mri) => mri.StartsWith("8:", StringComparison.Ordinal) ? mri[2..] : mri;

    private void ReadConversation(string file, JsonElement conversation, NormalizedIdentity? owner, IImportSink sink)
    {
        var id = Text(conversation, "id")
            ?? throw new InvalidDataException($"A conversation in '{file}' has no id.");

        var colon = id.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon > 0 ? id[..colon] : string.Empty;

        var kind = prefix switch
        {
            "8" or "28" or "4" or "2" => "dm",
            "19" => "group",
            "48" => null,
            _ => throw new InvalidDataException(
                $"Conversation '{id}' in '{file}' is of a kind this reader does not know."),
        };

        // Skype's own feeds: call logs and notifications. Their calls are already in the
        // conversations they happened in.
        if (kind is null)
        {
            return;
        }

        var displayName = Text(conversation, "displayName");
        var members = new List<NormalizedIdentity>();
        string? topic = null;

        if (conversation.TryGetProperty("threadProperties", out var thread) && thread.ValueKind == JsonValueKind.Object)
        {
            topic = Text(thread, "topic");

            // A JSON array, as a string inside the JSON.
            if (Text(thread, "members") is { } list)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(list);

                    members.AddRange(parsed.RootElement.EnumerateArray()
                        .Select(m => m.GetString())
                        .OfType<string>()
                        .Select(m => Person(Mri(m), null)));
                }
                catch (JsonException)
                {
                    throw new InvalidDataException($"Conversation '{id}' in '{file}' has a member list that is not JSON.");
                }
            }
        }

        var other = kind == "dm" ? Person(id, displayName) : null;

        if (other is not null)
        {
            members.Add(other);
        }

        var title = kind == "group" ? topic ?? displayName : displayName ?? Handle(id);
        var normalized = new NormalizedThread(id, kind, title, members);

        sink.OnThread(normalized);

        if (!conversation.TryGetProperty("MessageList", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var names = members.Where(m => m.DisplayName != m.Handle).ToDictionary(m => m.SourceIdentityId!, m => m.DisplayName, StringComparer.Ordinal);

        var ordered = messages.EnumerateArray()
            .Select(m => (Message: m, At: Arrival(m, file)))
            .OrderBy(m => m.At)
            .ToArray();

        foreach (var (message, at) in ordered)
        {
            if (ReadMessage(file, id, message, at, names, owner) is { } read)
            {
                sink.OnMessage(normalized, read);
            }
        }
    }

    private static DateTimeOffset Arrival(JsonElement message, string file)
    {
        var raw = Text(message, "originalarrivaltime")
            ?? throw new InvalidDataException($"A message in '{file}' has no originalarrivaltime.");

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime()
            : throw new InvalidDataException($"'{raw}' in '{file}' is not a time.");
    }

    private NormalizedMessage? ReadMessage(
        string file, string conversationId, JsonElement message, DateTimeOffset at,
        Dictionary<string, string> names, NormalizedIdentity? owner)
    {
        var type = Text(message, "messagetype")
            ?? throw new InvalidDataException($"A message in '{file}' has no messagetype.");

        if (Skipped.Contains(type))
        {
            return null;
        }

        var id = Text(message, "id")
            ?? throw new InvalidDataException($"A message in '{file}' has no id.");

        var from = Text(message, "from") is { } f ? Mri(f) : null;

        NormalizedIdentity? sender = null;

        if (from is not null)
        {
            sender = owner is not null && owner.SourceIdentityId == from
                ? owner
                : Person(from, Text(message, "displayName") ?? names.GetValueOrDefault(from));
        }

        var content = Text(message, "content") ?? string.Empty;

        string? service = null;
        string text;
        var media = new List<NormalizedMedia>();

        switch (type)
        {
            case "Text":
                text = content;
                break;

            case "RichText":
            case "RichText/Html":
            case "RichText/Location":
            case "RichText/Contacts":
            case "RichText/Media_Card":
            case "RichText/Sms":
                text = Plain(content, media, type);
                break;

            case "RichText/UriObject":
            case "RichText/Media_Video":
            case "RichText/Media_AudioMsg":
            case "RichText/Media_GenericFile":
            case "RichText/Media_FlikMsg":
                text = Plain(content, media, type);
                break;

            case "Event/Call":
                service = "phone_call";
                text = string.Empty;
                break;

            case "ThreadActivity/AddMember":
                service = "invite_members";
                text = string.Empty;
                break;

            case "ThreadActivity/DeleteMember":
                service = "remove_members";
                text = string.Empty;
                break;

            case "ThreadActivity/TopicUpdate":
                service = "edit_group_title";
                text = string.Empty;
                break;

            case "ThreadActivity/PictureUpdate":
                service = "edit_group_photo";
                text = string.Empty;
                break;

            case var other when other.StartsWith("ThreadActivity/", StringComparison.Ordinal):
                service = other["ThreadActivity/".Length..].ToLowerInvariant();
                text = string.Empty;
                break;

            default:
                throw new InvalidDataException(
                    $"A message in conversation '{conversationId}' of '{file}' has type '{type}', which this reader does not know.");
        }

        string? edited = null;

        if (message.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            if (Millis(properties, "edittime") is { } editedAt)
            {
                edited = editedAt.ToString("O", CultureInfo.InvariantCulture);
            }

            // A deleted message keeps its place and loses its words, which is what Skype showed.
            if (Millis(properties, "deletetime") is not null)
            {
                text = string.Empty;
                media.Clear();
            }
        }

        return new NormalizedMessage
        {
            Uid = $"sk/{conversationId}/{id}",
            SourceThreadId = conversationId,
            Kind = service is null ? "message" : "service",
            Sender = sender,
            ServiceAction = service,
            SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
            SentAtUnix = at.ToUnixTimeSeconds(),
            Plaintext = text,
            EntitiesJson = content.Contains('<', StringComparison.Ordinal) ? JsonSerializer.Serialize(content) : null,
            ContentHash = Hash($"{text}{(char)31}{content}"),
            EditedAtUtc = edited,
            RawJson = message.GetRawText(),
            Media = media,
        };
    }

    private static DateTimeOffset? Millis(JsonElement properties, string name)
    {
        if (!properties.TryGetProperty(name, out var value))
        {
            return null;
        }

        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
    }

    /// <summary>
    /// What a person saw: the text of Skype's markup, with attachments lifted out.
    /// </summary>
    internal static string Plain(string content, List<NormalizedMedia> media, string type = "RichText")
    {
        XElement root;

        try
        {
            // &nbsp; is the one HTML entity Skype writes that XML does not know.
            root = XElement.Parse(
                "<root>" + content.Replace("&nbsp;", "&#160;", StringComparison.Ordinal) + "</root>",
                LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return WebUtility.HtmlDecode(Tag().Replace(content, string.Empty)).Trim();
        }

        var builder = new StringBuilder();
        Walk(root, builder, media, type);

        return builder.ToString().Trim();
    }

    private static void Walk(XElement element, StringBuilder builder, List<NormalizedMedia> media, string type)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;

                case XElement child:
                    switch (child.Name.LocalName.ToLowerInvariant())
                    {
                        // A copy of the quote's own header and footer, for clients that cannot
                        // render quotes; and the edit marker.
                        case "legacyquote":
                        case "e_m":
                            break;

                        case "uriobject":
                            media.Add(Attachment(child, type));
                            break;

                        case "quote":
                            Walk(child, builder, media, type);

                            if (builder.Length > 0 && builder[^1] != '\n')
                            {
                                builder.Append('\n');
                            }

                            break;

                        case "br":
                            builder.Append('\n');
                            break;

                        default:
                            Walk(child, builder, media, type);
                            break;
                    }

                    break;
            }
        }
    }

    private static NormalizedMedia Attachment(XElement uriObject, string type)
    {
        var name = uriObject.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("OriginalName", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("v")?.Value;

        var uri = uriObject.Attribute("uri")?.Value;

        var kind = type switch
        {
            "RichText/Media_Video" or "RichText/Media_FlikMsg" => "video",
            "RichText/Media_AudioMsg" => "voice",
            "RichText/Media_GenericFile" => "file",
            _ => (uriObject.Attribute("type")?.Value ?? string.Empty).StartsWith("Picture", StringComparison.OrdinalIgnoreCase) ? "photo" : "file",
        };

        return new NormalizedMedia(
            uri ?? name ?? "attachment", kind, name, Mime: null,
            MissingReason: "Skype's export links to shared files rather than including them",
            StickerEmoji: null, Width: null, Height: null, DurationSeconds: null);
    }

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
