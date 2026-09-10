using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archive.Import.Synthetic;

/// <summary>One Hangouts account: a gaia id and the name the export falls back to.</summary>
public sealed record HangoutsAccount(string GaiaId, string? FallbackName = null);

/// <summary>One run of a message's content.</summary>
/// <remarks>
/// A LINE_BREAK carries no text at all, which is exactly the trap: concatenating only the text
/// fields runs paragraphs together into one line.
/// </remarks>
public sealed record HangoutsSegment(string Type, string? Text = null, string? LinkTarget = null)
{
    public static HangoutsSegment Text_(string text) => new("TEXT", text);

    public static HangoutsSegment LineBreak { get; } = new("LINE_BREAK");

    public static HangoutsSegment Link(string text, string target) => new("LINK", text, target);
}

/// <summary>
/// Builds a Google Takeout <c>Hangouts.json</c>, as a real file on disk.
/// </summary>
/// <remarks>
/// Hangouts is dead and therefore frozen, so this shape will not move again. See
/// <see cref="TelegramExportBuilder"/> for why exports are built in code rather than committed.
/// </remarks>
public sealed class HangoutsExportBuilder
{
    private readonly JsonArray _conversations = [];

    public static HangoutsExportBuilder New() => new();

    /// <summary>
    /// One conversation.
    /// </summary>
    /// <param name="self">
    /// The account whose export this is, written into <c>self_conversation_state</c>. Null leaves
    /// it out, which is what forces the owner to be inferred instead of read.
    /// </param>
    public HangoutsExportBuilder Conversation(
        string id,
        string type,
        IReadOnlyList<HangoutsAccount> participants,
        Action<HangoutsConversationBuilder> events,
        HangoutsAccount? self = null,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(events);

        var inner = new JsonObject
        {
            ["id"] = new JsonObject { ["id"] = id },
            ["type"] = type,
        };

        if (name is not null)
        {
            inner["name"] = name;
        }

        if (self is not null)
        {
            inner["self_conversation_state"] = new JsonObject
            {
                ["self_read_state"] = new JsonObject
                {
                    ["participant_id"] = new JsonObject { ["gaia_id"] = self.GaiaId },
                },
            };
        }

        var roster = new JsonArray();

        foreach (var participant in participants)
        {
            var entry = new JsonObject { ["id"] = new JsonObject { ["gaia_id"] = participant.GaiaId } };

            if (participant.FallbackName is not null)
            {
                entry["fallback_name"] = participant.FallbackName;
            }

            roster.Add(entry);
        }

        inner["participant_data"] = roster;

        var array = new JsonArray();
        events(new HangoutsConversationBuilder(array));

        _conversations.Add(new JsonObject
        {
            ["conversation"] = new JsonObject
            {
                ["conversation_id"] = new JsonObject { ["id"] = id },
                ["conversation"] = inner,
            },
            ["events"] = array,
        });

        return this;
    }

    public string Json() =>
        new JsonObject { ["conversations"] = _conversations }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes <c>Hangouts.json</c> into <paramref name="folder"/> and returns the folder.</summary>
    public string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Hangouts.json"), Json());

        return folder;
    }
}

/// <summary>The events of one conversation.</summary>
public sealed class HangoutsConversationBuilder(JsonArray events)
{
    public HangoutsConversationBuilder Message(
        string eventId, DateTimeOffset at, HangoutsAccount sender, params HangoutsSegment[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var array = new JsonArray();

        foreach (var segment in segments)
        {
            var node = new JsonObject { ["type"] = segment.Type };

            if (segment.Text is not null)
            {
                node["text"] = segment.Text;
            }

            if (segment.LinkTarget is not null)
            {
                node["link_data"] = new JsonObject { ["link_target"] = segment.LinkTarget };
            }

            array.Add(node);
        }

        var e = Event(eventId, at, sender);
        e["chat_message"] = new JsonObject
        {
            ["message_content"] = new JsonObject { ["segment"] = array },
        };

        events.Add(e);

        return this;
    }

    /// <summary>Plain text, for the common case where the segment array is not the point.</summary>
    public HangoutsConversationBuilder Message(
        string eventId, DateTimeOffset at, HangoutsAccount sender, string text) =>
        Message(eventId, at, sender, HangoutsSegment.Text_(text));

    /// <summary>
    /// A call.
    /// </summary>
    /// <remarks>
    /// Calls are events too, and dropping them makes §8's "four months, no contact" a lie in every
    /// conversation that was spoken rather than typed.
    /// </remarks>
    public HangoutsConversationBuilder Hangout(
        string eventId, DateTimeOffset at, HangoutsAccount sender, string eventType)
    {
        var e = Event(eventId, at, sender);
        e["hangout_event"] = new JsonObject { ["event_type"] = eventType };

        events.Add(e);

        return this;
    }

    public HangoutsConversationBuilder MembershipChange(
        string eventId, DateTimeOffset at, HangoutsAccount sender, string type)
    {
        var e = Event(eventId, at, sender);
        e["membership_change"] = new JsonObject { ["type"] = type };

        events.Add(e);

        return this;
    }

    /// <summary>
    /// An event with no <c>event_id</c>.
    /// </summary>
    /// <remarks>
    /// Every real event has one, and its absence means the file is not the shape the reader thinks
    /// it is. Building it deliberately is how the refusal gets tested.
    /// </remarks>
    public HangoutsConversationBuilder MessageWithNoEventId(
        DateTimeOffset at, HangoutsAccount sender, string text)
    {
        events.Add(new JsonObject
        {
            ["sender_id"] = new JsonObject { ["gaia_id"] = sender.GaiaId },
            ["timestamp"] = Microseconds(at),
            ["chat_message"] = new JsonObject
            {
                ["message_content"] = new JsonObject
                {
                    ["segment"] = new JsonArray(new JsonObject { ["type"] = "TEXT", ["text"] = text }),
                },
            },
        });

        return this;
    }

    private static JsonObject Event(string eventId, DateTimeOffset at, HangoutsAccount sender)
    {
        ArgumentNullException.ThrowIfNull(sender);

        return new JsonObject
        {
            ["event_id"] = eventId,
            ["sender_id"] = new JsonObject { ["gaia_id"] = sender.GaiaId },
            ["timestamp"] = Microseconds(at),
        };
    }

    /// <summary>
    /// Hangouts timestamps are microseconds since the epoch, as a decimal string.
    /// </summary>
    /// <remarks>
    /// The trailing digits are not zero on purpose: a reader that divides by the wrong power of a
    /// thousand should land somewhere obviously wrong rather than somewhere plausible.
    /// </remarks>
    private static string Microseconds(DateTimeOffset at) =>
        ((at.ToUnixTimeSeconds() * 1_000_000L) + 123_456L).ToString(CultureInfo.InvariantCulture);
}
