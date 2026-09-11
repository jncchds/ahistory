using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archive.Import.Synthetic;

/// <summary>One Slack account: an id, a username and a real name.</summary>
public sealed record SlackUser(string Id, string Name, string RealName);

/// <summary>
/// Builds a Slack workspace export, as real files on disk.
/// </summary>
/// <remarks>
/// Messages are filed into one JSON file per UTC day, as Slack files them, so a conversation that
/// crosses midnight is split across files the way a real one is.
/// </remarks>
public sealed class SlackExportBuilder
{
    private readonly IReadOnlyList<SlackUser> _users;
    private readonly List<(string File, JsonObject Entry, string Folder, List<(DateTimeOffset At, JsonObject Node)> Messages)> _conversations = [];

    private SlackExportBuilder(IReadOnlyList<SlackUser> users) => _users = users;

    public static SlackExportBuilder New(IReadOnlyList<SlackUser> users) => new(users);

    public SlackExportBuilder Channel(
        string id, string name, IReadOnlyList<string> members, Action<SlackConversationBuilder> messages, bool isPrivate = false) =>
        Add(isPrivate ? "groups.json" : "channels.json", new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["created"] = 1615000000,
            ["members"] = Members(members),
        }, name, messages);

    public SlackExportBuilder Dm(string id, IReadOnlyList<string> members, Action<SlackConversationBuilder> messages) =>
        Add("dms.json", new JsonObject { ["id"] = id, ["created"] = 1615000000, ["members"] = Members(members) }, id, messages);

    public SlackExportBuilder Mpim(string id, string name, IReadOnlyList<string> members, Action<SlackConversationBuilder> messages) =>
        Add("mpims.json", new JsonObject { ["id"] = id, ["name"] = name, ["created"] = 1615000000, ["members"] = Members(members) }, name, messages);

    private static JsonArray Members(IReadOnlyList<string> members) => new([.. members.Select(m => (JsonNode)m)]);

    private SlackExportBuilder Add(string file, JsonObject entry, string folder, Action<SlackConversationBuilder> messages)
    {
        var list = new List<(DateTimeOffset, JsonObject)>();
        messages(new SlackConversationBuilder(list));
        _conversations.Add((file, entry, folder, list));

        return this;
    }

    /// <summary>Writes the export into <paramref name="root"/> and returns it.</summary>
    public string Write(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);

        var options = new JsonSerializerOptions { WriteIndented = true };

        File.WriteAllText(Path.Combine(root, "users.json"), new JsonArray([.. _users.Select(u => (JsonNode)new JsonObject
        {
            ["id"] = u.Id,
            ["name"] = u.Name,
            ["real_name"] = u.RealName,
            ["deleted"] = false,
            ["is_bot"] = false,
            ["profile"] = new JsonObject { ["real_name"] = u.RealName, ["display_name"] = u.Name },
        })]).ToJsonString(options));

        // channels.json is always there, even empty: it is half of what makes a folder an export.
        foreach (var file in new[] { "channels.json" }.Concat(_conversations.Select(c => c.File)).Distinct())
        {
            File.WriteAllText(
                Path.Combine(root, file),
                new JsonArray([.. _conversations.Where(c => c.File == file).Select(c => (JsonNode)c.Entry.DeepClone())]).ToJsonString(options));
        }

        foreach (var (_, _, folder, messages) in _conversations)
        {
            var directory = Path.Combine(root, folder);
            Directory.CreateDirectory(directory);

            foreach (var day in messages.GroupBy(m => m.At.UtcDateTime.Date))
            {
                File.WriteAllText(
                    Path.Combine(directory, day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json"),
                    new JsonArray([.. day.Select(m => (JsonNode)m.Node.DeepClone())]).ToJsonString(options));
            }
        }

        return root;
    }
}

/// <summary>The messages of one conversation.</summary>
public sealed class SlackConversationBuilder(List<(DateTimeOffset At, JsonObject Node)> messages)
{
    /// <param name="ts">Slack's own id and time together, e.g. <c>1615757463.000100</c>.</param>
    public SlackConversationBuilder Message(string ts, string user, string text, Action<SlackMessageBuilder>? more = null)
    {
        var node = new JsonObject { ["type"] = "message", ["user"] = user, ["text"] = text, ["ts"] = ts };

        more?.Invoke(new SlackMessageBuilder(node));
        messages.Add((At(ts), node));

        return this;
    }

    /// <summary>A message with a subtype: a join, a topic change, or one nobody knows.</summary>
    public SlackConversationBuilder Subtype(string ts, string user, string subtype, string text)
    {
        messages.Add((At(ts), new JsonObject { ["type"] = "message", ["subtype"] = subtype, ["user"] = user, ["text"] = text, ["ts"] = ts }));

        return this;
    }

    public SlackConversationBuilder Bot(string ts, string botId, string username, string text)
    {
        messages.Add((At(ts), new JsonObject
        {
            ["type"] = "message",
            ["subtype"] = "bot_message",
            ["bot_id"] = botId,
            ["username"] = username,
            ["text"] = text,
            ["ts"] = ts,
        }));

        return this;
    }

    private static DateTimeOffset At(string ts) =>
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(ts[..ts.IndexOf('.', StringComparison.Ordinal)], CultureInfo.InvariantCulture));
}

/// <summary>What else one message carries.</summary>
public sealed class SlackMessageBuilder(JsonObject message)
{
    public SlackMessageBuilder Reply(string parentTs)
    {
        message["thread_ts"] = parentTs;

        return this;
    }

    public SlackMessageBuilder Reaction(string name, int count, params string[] users)
    {
        if (message["reactions"] is not JsonArray reactions)
        {
            reactions = [];
            message["reactions"] = reactions;
        }

        reactions.Add(new JsonObject
        {
            ["name"] = name,
            ["users"] = new JsonArray([.. users.Select(u => (JsonNode)u)]),
            ["count"] = count,
        });

        return this;
    }

    public SlackMessageBuilder File(string name, string mimetype)
    {
        message["files"] = new JsonArray(new JsonObject
        {
            ["id"] = "F0001",
            ["name"] = name,
            ["title"] = name,
            ["mimetype"] = mimetype,
            ["url_private"] = $"https://files.slack.com/files-pri/T0001-F0001/{name}",
        });

        return this;
    }

    public SlackMessageBuilder Edited(string ts)
    {
        message["edited"] = new JsonObject { ["user"] = message["user"]?.DeepClone(), ["ts"] = ts };

        return this;
    }
}
