using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archive.Import.Synthetic;

/// <summary>
/// Builds the <c>messages.json</c> inside a Skype export, as a real file on disk.
/// </summary>
/// <remarks>
/// Content is written as the markup Skype writes, because the markup is the format: a builder
/// that wrote plain text would test a reader nobody needs.
/// </remarks>
public sealed class SkypeExportBuilder
{
    private readonly string _userId;
    private readonly JsonArray _conversations = [];

    private SkypeExportBuilder(string userId) => _userId = userId;

    public static SkypeExportBuilder New(string userId) => new(userId);

    public SkypeExportBuilder Conversation(
        string id,
        string? displayName,
        Action<SkypeConversationBuilder> messages,
        string? topic = null,
        IReadOnlyList<string>? members = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        JsonNode? threadProperties = null;

        if (topic is not null || members is not null)
        {
            var props = new JsonObject { ["membercount"] = members?.Count ?? 0 };

            if (topic is not null)
            {
                props["topic"] = topic;
            }

            if (members is not null)
            {
                // Skype writes the member list as a JSON array inside a string.
                props["members"] = JsonSerializer.Serialize(members);
            }

            threadProperties = props;
        }

        var list = new JsonArray();
        messages(new SkypeConversationBuilder(list, id));

        _conversations.Add(new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["version"] = 1615757463000,
            ["properties"] = new JsonObject { ["conversationblocked"] = false },
            ["threadProperties"] = threadProperties,
            ["MessageList"] = list,
        });

        return this;
    }

    public string Json() =>
        new JsonObject
        {
            ["userId"] = _userId,
            ["exportDate"] = "2021-03-20T10:00:00.000Z",
            ["conversations"] = _conversations.DeepClone(),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes <c>messages.json</c> into <paramref name="folder"/> and returns the folder.</summary>
    public string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "messages.json"), Json());

        return folder;
    }
}

/// <summary>The messages of one conversation.</summary>
public sealed class SkypeConversationBuilder(JsonArray messages, string conversationId)
{
    /// <param name="from">A bare id, or the URL form older exports used.</param>
    /// <param name="editedAt">Written to <c>properties.edittime</c>, with the <c>e_m</c> marker in the content.</param>
    public SkypeConversationBuilder Message(
        string id,
        DateTimeOffset at,
        string from,
        string? displayName,
        string content,
        string type = "RichText",
        DateTimeOffset? editedAt = null,
        bool deleted = false)
    {
        var node = Node(id, at, from, displayName, type, content);

        if (editedAt is not null || deleted)
        {
            var properties = new JsonObject();

            if (editedAt is { } edited)
            {
                properties["edittime"] = edited.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                node["content"] = content + $"<e_m ts=\"{edited.ToUnixTimeSeconds()}\" a=\"{from}\" t=\"61\"/>";
            }

            if (deleted)
            {
                properties["deletetime"] = at.AddMinutes(1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            }

            node["properties"] = properties;
        }

        messages.Add(node);

        return this;
    }

    public SkypeConversationBuilder Call(string id, DateTimeOffset at, string from, string name) =>
        Message(id, at, from, null,
            $"<partlist type=\"started\" alt=\"\"><part identity=\"{from}\"><name>{name}</name></part></partlist>",
            "Event/Call");

    public SkypeConversationBuilder AddMember(string id, DateTimeOffset at, string initiator, string target) =>
        Message(id, at, initiator, null,
            $"<addmember><eventtime>{at.ToUnixTimeMilliseconds()}</eventtime><initiator>{initiator}</initiator><target>{target}</target></addmember>",
            "ThreadActivity/AddMember");

    /// <summary>A shared picture, which the export links to rather than includes.</summary>
    public SkypeConversationBuilder Picture(string id, DateTimeOffset at, string from, string originalName) =>
        Message(id, at, from, null,
            $"<URIObject type=\"Picture.1\" uri=\"https://api.asm.skype.com/v1/objects/0-weu-d1-{id}\" "
            + $"url_thumbnail=\"https://api.asm.skype.com/v1/objects/0-weu-d1-{id}/views/imgt1\">"
            + "To view this shared photo, go to: <a href=\"https://login.skype.com/login/sso?go=webclient.xmm\">link</a>"
            + $"<OriginalName v=\"{originalName}\"/><FileSize v=\"2048\"/><meta type=\"photo\" originalName=\"{originalName}\"/></URIObject>",
            "RichText/UriObject");

    private JsonObject Node(string id, DateTimeOffset at, string from, string? displayName, string type, string content) => new()
    {
        ["id"] = id,
        ["displayName"] = displayName,
        ["originalarrivaltime"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ["messagetype"] = type,
        ["version"] = at.ToUnixTimeMilliseconds(),
        ["content"] = content,
        ["conversationid"] = conversationId,
        ["from"] = from,
        ["properties"] = null,
        ["amsreferences"] = null,
    };
}
