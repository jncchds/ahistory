using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Archive.Import.Hangouts;

/// <summary>
/// Google Hangouts, as Takeout exported it.
/// </summary>
/// <remarks>
/// <para>
/// One large <c>Hangouts.json</c>: a <c>conversations</c> array, each entry pairing a
/// <c>conversation</c> block (participants, id, type) with an <c>events</c> array. Older exports
/// wrap each entry in <c>conversation_state</c> and name the top-level array
/// <c>conversation_state</c> too; both shapes are read, because an archive of old conversations
/// is exactly where the old shape turns up.
/// </para>
/// <para>
/// Traps this format has, none of which Telegram shares:
/// </para>
/// <list type="bullet">
///   <item>Timestamps are <b>microseconds</b> since the epoch, as a string. Read as milliseconds
///   every message lands in 1970.</item>
///   <item>Message text is a <c>segment</c> array of typed runs — TEXT, LINK, LINE_BREAK — and a
///   LINE_BREAK carries no text, so concatenating only the text fields silently glues paragraphs
///   together.</item>
///   <item>Senders are <c>gaia_id</c>, a Google account id. Names live only in the conversation's
///   participant list, so a sender has to be resolved against it.</item>
///   <item>Hangouts had no message ids. The event id is the only stable key, and events with no
///   <c>chat_message</c> at all — calls, people joining — are the majority in some conversations.</item>
/// </list>
/// <para>
/// Hangouts was shut down in 2022, so this reads archives rather than anything current: the format
/// will not change again, which is the one advantage of a dead product.
/// </para>
/// </remarks>
public sealed class HangoutsImporter : IPlatformImporter
{
    public const string PlatformId = "hangouts";

    public string Platform => PlatformId;

    public string DisplayName => "Google Hangouts";

    public ImportDetection Detect(string path)
    {
        var file = FindExport(path);

        if (file is null)
        {
            return ImportDetection.No;
        }

        // The filename is Takeout's own, and the top-level key confirms which vintage it is
        // without reading the rest of what can be a very large file.
        var shape = PeekShape(file);

        if (shape is null)
        {
            return ImportDetection.No;
        }

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: null,
            AccountName: null,
            FileCount: 1,
            Note: "The account is read from the export itself while importing, so this folder "
                + "starts a new source. If it is a newer export of one you already have, pick "
                + "that source instead.");
    }

    public void Read(string path, IImportSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var file = FindExport(path)
            ?? throw new InvalidDataException(
                $"'{path}' contains no Hangouts.json. Take the Hangouts folder from Google Takeout as JSON.");

        using var document = JsonDocument.Parse(
            File.ReadAllBytes(file),
            new JsonDocumentOptions { AllowTrailingCommas = true });

        var conversations = FindConversations(document.RootElement)
            ?? throw new InvalidDataException(
                $"'{file}' has no conversations array. This does not look like a Hangouts export.");

        // Resolved first, because every message needs it to know which side of the conversation
        // it belongs on.
        var owner = InferOwner(conversations);

        if (owner is not null)
        {
            sink.OnOwner(owner);
        }

        foreach (var entry in conversations.EnumerateArray())
        {
            ReadConversation(Unwrap(entry), sink);
        }
    }

    /// <summary>Takeout's own filename, at the folder root or one level down.</summary>
    private static string? FindExport(string path)
    {
        if (File.Exists(path) && Path.GetFileName(path).Equals("Hangouts.json", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        return Directory.EnumerateFiles(path, "Hangouts.json", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    /// <summary>Reads only the first key, to tell an export from any other large JSON file.</summary>
    private static string? PeekShape(string file)
    {
        using var stream = File.OpenRead(file);
        var buffer = new byte[4096];
        var read = stream.Read(buffer);

        var head = Encoding.UTF8.GetString(buffer, 0, read);

        if (head.Contains("\"conversations\"", StringComparison.Ordinal))
        {
            return "conversations";
        }

        return head.Contains("\"conversation_state\"", StringComparison.Ordinal)
            ? "conversation_state"
            : null;
    }

    private static JsonElement? FindConversations(JsonElement root)
    {
        foreach (var key in new[] { "conversations", "conversation_state" })
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Older exports nest each entry inside another <c>conversation_state</c>.</summary>
    private static JsonElement Unwrap(JsonElement entry) =>
        entry.TryGetProperty("conversation_state", out var inner) && inner.ValueKind == JsonValueKind.Object
            ? inner
            : entry;

    /// <summary>
    /// The account the export belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every conversation carries a <c>self_conversation_state</c> naming the account whose export
    /// this is. That is the whole answer, and it is stated rather than inferred — which matters,
    /// because getting the owner wrong puts your own messages on the far side of every
    /// conversation you have ever had.
    /// </para>
    /// <para>
    /// Falling back to "the participant present in every conversation" only when nothing states
    /// it, and only when that is unambiguous. In a two-conversation export both people are
    /// usually in both, so it resolves nothing and nothing is claimed — the user attributes the
    /// account themselves rather than being told a guess.
    /// </para>
    /// </remarks>
    private static NormalizedIdentity? InferOwner(JsonElement conversations)
    {
        var seen = new Dictionary<string, (int Count, string? Name)>(StringComparer.Ordinal);
        var total = 0;

        foreach (var entry in conversations.EnumerateArray())
        {
            var conversation = Conversation(Unwrap(entry));

            if (conversation is not { } block)
            {
                continue;
            }

            total++;

            var names = Participants(block).ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);

            // Stated outright: no need to infer anything.
            if (SelfId(block) is { } self)
            {
                return new NormalizedIdentity(
                    PlatformId, self, Handle: null, names.GetValueOrDefault(self) ?? self, IsSynthetic: false);
            }

            foreach (var (id, name) in names)
            {
                var existing = seen.GetValueOrDefault(id);
                seen[id] = (existing.Count + 1, existing.Name ?? name);
            }
        }

        if (total < 2)
        {
            return null;
        }

        var candidates = seen.Where(p => p.Value.Count == total).ToArray();

        if (candidates.Length != 1)
        {
            return null;
        }

        var (ownerId, owner) = (candidates[0].Key, candidates[0].Value);

        return new NormalizedIdentity(
            PlatformId, ownerId, Handle: null, owner.Name ?? ownerId, IsSynthetic: false);
    }

    /// <summary>The gaia id inside <c>self_conversation_state</c>, when the export carries one.</summary>
    private static string? SelfId(JsonElement conversation)
    {
        if (!conversation.TryGetProperty("self_conversation_state", out var self) ||
            self.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (self.TryGetProperty("self_read_state", out var readState) &&
            readState.ValueKind == JsonValueKind.Object &&
            readState.TryGetProperty("participant_id", out var participant))
        {
            return GaiaId(participant);
        }

        return null;
    }

    private static JsonElement? Conversation(JsonElement entry) =>
        entry.TryGetProperty("conversation", out var outer) && outer.ValueKind == JsonValueKind.Object
            ? outer.TryGetProperty("conversation", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner
                : outer
            : null;

    private static IEnumerable<(string Id, string? Name)> Participants(JsonElement conversation)
    {
        if (!conversation.TryGetProperty("participant_data", out var participants) ||
            participants.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var participant in participants.EnumerateArray())
        {
            var id = GaiaId(participant);

            if (id is not null)
            {
                yield return (id, Text(participant, "fallback_name"));
            }
        }
    }

    private static string? GaiaId(JsonElement element) =>
        element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Object
            ? Text(id, "gaia_id")
            : Text(element, "gaia_id");

    private void ReadConversation(JsonElement entry, IImportSink sink)
    {
        var conversation = Conversation(entry);

        if (conversation is not { } block)
        {
            return;
        }

        var id = ConversationId(block)
            ?? throw new InvalidDataException("A Hangouts conversation has no id.");

        var names = Participants(block)
            .ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);

        var type = Text(block, "type");

        // STICKY_ONE_TO_ONE is Hangouts' name for a direct conversation; GROUP is a group.
        var kind = type switch
        {
            "STICKY_ONE_TO_ONE" => "dm",
            "GROUP" => "group",
            _ when names.Count <= 2 => "dm",
            _ => "group",
        };

        var thread = new NormalizedThread(id, kind, ConversationTitle(block, names, kind));
        sink.OnThread(thread);

        if (!entry.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var e in events.EnumerateArray())
        {
            var message = ReadEvent(e, id, names);

            if (message is not null)
            {
                sink.OnMessage(thread, message);
            }
        }
    }

    private static string? ConversationId(JsonElement conversation)
    {
        if (conversation.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Object)
        {
            return Text(id, "id");
        }

        return Text(conversation, "id");
    }

    /// <summary>
    /// A name for the conversation.
    /// </summary>
    /// <remarks>
    /// Group conversations may carry one; direct ones never do, so they are named after the other
    /// participant — which is what the app shows in the thread list and what a user recognizes.
    /// </remarks>
    private static string? ConversationTitle(
        JsonElement conversation, Dictionary<string, string?> names, string kind)
    {
        if (conversation.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        return kind == "dm"
            ? names.Values.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            : null;
    }

    private NormalizedMessage? ReadEvent(
        JsonElement e, string conversationId, Dictionary<string, string?> names)
    {
        var eventId = Text(e, "event_id");

        if (eventId is null)
        {
            // Every event has one. Its absence means this is not the shape we think it is, and
            // inventing a key would make the import non-idempotent — the same export would
            // duplicate itself on every run.
            throw new InvalidDataException("A Hangouts event has no event_id.");
        }

        var senderId = e.TryGetProperty("sender_id", out var sender) ? GaiaId(sender) : null;
        var (sentAtUtc, sentAtUnix) = ReadTimestamp(e);

        var isChat = e.TryGetProperty("chat_message", out var chat) && chat.ValueKind == JsonValueKind.Object;

        // Calls, renames and people joining are events too. They become service messages rather
        // than being dropped: "four months, no contact" (§8) is only true if a call in between
        // was not silently discarded.
        var serviceAction = isChat ? null : ServiceAction(e);

        if (!isChat && serviceAction is null)
        {
            return null;
        }

        var (plaintext, entitiesJson) = isChat ? ReadSegments(chat) : (string.Empty, null);

        var identity = senderId is null
            ? null
            : new NormalizedIdentity(
                PlatformId, senderId, Handle: null,
                names.GetValueOrDefault(senderId) ?? senderId, IsSynthetic: false);

        return new NormalizedMessage
        {
            Uid = $"gh/{conversationId}/{eventId}",
            SourceThreadId = conversationId,
            Kind = isChat ? "message" : "service",
            Sender = identity,
            ServiceAction = serviceAction,
            SentAtUtc = sentAtUtc,
            SentAtUnix = sentAtUnix,
            Plaintext = plaintext,
            EntitiesJson = entitiesJson,
            ContentHash = ContentHash(plaintext, entitiesJson, serviceAction),
            RawJson = e.GetRawText(),
        };
    }

    private static string? ServiceAction(JsonElement e)
    {
        if (e.TryGetProperty("hangout_event", out var hangout) && hangout.ValueKind == JsonValueKind.Object)
        {
            return Text(hangout, "event_type") switch
            {
                "START_HANGOUT" => "call_started",
                "END_HANGOUT" => "call_ended",
                var other => other?.ToLowerInvariant() ?? "hangout_event",
            };
        }

        if (e.TryGetProperty("membership_change", out var membership) && membership.ValueKind == JsonValueKind.Object)
        {
            return Text(membership, "type") switch
            {
                "JOIN" => "invite_members",
                "LEAVE" => "remove_members",
                var other => other?.ToLowerInvariant() ?? "membership_change",
            };
        }

        return e.TryGetProperty("conversation_rename", out _) ? "edit_group_title" : null;
    }

    /// <summary>
    /// Timestamps are microseconds since the epoch, as a decimal string.
    /// </summary>
    /// <remarks>
    /// Read as milliseconds — the obvious assumption, and what most code does — every message in
    /// the archive lands in January 1970, sorted into one indistinguishable heap.
    /// </remarks>
    private static (string Utc, long Unix) ReadTimestamp(JsonElement e)
    {
        var raw = Text(e, "timestamp")
            ?? throw new InvalidDataException("A Hangouts event has no timestamp.");

        if (!long.TryParse(raw, CultureInfo.InvariantCulture, out var micros))
        {
            throw new InvalidDataException($"A Hangouts timestamp is not a number: '{raw}'.");
        }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000);

        return (at.ToString("O"), at.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Message text, from the typed run array.
    /// </summary>
    /// <remarks>
    /// A LINE_BREAK segment carries no <c>text</c> field, so concatenating only the text values
    /// runs paragraphs together into one line. It is emitted as a newline instead. The runs are
    /// kept as entities for the same reason Telegram's are — links are the part plaintext loses.
    /// </remarks>
    private static (string Plaintext, string? EntitiesJson) ReadSegments(JsonElement chat)
    {
        if (!chat.TryGetProperty("message_content", out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("segment", out var segments) ||
            segments.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, null);
        }

        var builder = new StringBuilder();

        foreach (var segment in segments.EnumerateArray())
        {
            switch (Text(segment, "type"))
            {
                case "LINE_BREAK":
                    builder.Append('\n');
                    break;

                case "LINK":
                    // The visible text, falling back to the target when a link has no label.
                    builder.Append(Text(segment, "text") ?? LinkTarget(segment) ?? string.Empty);
                    break;

                default:
                    builder.Append(Text(segment, "text") ?? string.Empty);
                    break;
            }
        }

        return (builder.ToString(), segments.GetArrayLength() > 0 ? segments.GetRawText() : null);
    }

    private static string? LinkTarget(JsonElement segment) =>
        segment.TryGetProperty("link_data", out var link) && link.ValueKind == JsonValueKind.Object
            ? Text(link, "link_target")
            : null;

    private static string ContentHash(string plaintext, string? entitiesJson, string? serviceAction)
    {
        const char Separator = (char)31;

        var material = new StringBuilder()
            .Append(plaintext).Append(Separator)
            .Append(entitiesJson).Append(Separator)
            .Append(serviceAction)
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string? Text(JsonElement element, string property)
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
}
