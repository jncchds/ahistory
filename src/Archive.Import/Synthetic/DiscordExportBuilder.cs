using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archive.Import.Synthetic;

/// <summary>One Discord account: a snowflake id, a username, and the display name if set.</summary>
public sealed record DiscordUser(string Id, string Username, string? GlobalName = null);

/// <summary>
/// Builds a Discord data package ("Request all of my data"), as real files on disk.
/// </summary>
/// <remarks>
/// Only the owner's messages, as Discord gives them. Channels are written as CSV unless asked for
/// the newer JSON, and the CSV is quoted the way Discord quotes it, so a message with a comma, a
/// quote or a newline in it is exactly as awkward as a real one.
/// </remarks>
public sealed class DiscordPackageBuilder
{
    private readonly DiscordUser? _owner;
    private readonly List<(string Id, JsonObject Channel, string? Index, bool Json, List<(string Id, DateTimeOffset At, string Contents, string Attachments)> Rows)> _channels = [];

    private DiscordPackageBuilder(DiscordUser? owner) => _owner = owner;

    /// <param name="owner">Written to <c>account/user.json</c>; null leaves the account folder out.</param>
    public static DiscordPackageBuilder New(DiscordUser? owner) => new(owner);

    public DiscordPackageBuilder Dm(
        string channelId, DiscordUser owner, DiscordUser other, Action<DiscordPackageChannelBuilder> messages, bool json = false) =>
        Channel(channelId, new JsonObject
        {
            ["id"] = channelId,
            ["type"] = 1,
            ["recipients"] = new JsonArray(owner.Id, other.Id),
        }, $"Direct Message with {other.Username}#0", json, messages);

    public DiscordPackageBuilder GroupDm(
        string channelId, string? name, IReadOnlyList<DiscordUser> recipients, Action<DiscordPackageChannelBuilder> messages, bool json = false)
    {
        var channel = new JsonObject
        {
            ["id"] = channelId,
            ["type"] = 3,
            ["recipients"] = new JsonArray([.. recipients.Select(r => (JsonNode)r.Id)]),
        };

        if (name is not null)
        {
            channel["name"] = name;
        }

        return Channel(channelId, channel, name, json, messages);
    }

    public DiscordPackageBuilder GuildChannel(
        string channelId, string guildName, string name, Action<DiscordPackageChannelBuilder> messages, bool json = false, int type = 0) =>
        Channel(channelId, new JsonObject
        {
            ["id"] = channelId,
            ["type"] = type,
            ["name"] = name,
            ["guild"] = new JsonObject { ["id"] = "900000000000000001", ["name"] = guildName },
        }, $"{name} in {guildName}", json, messages);

    private DiscordPackageBuilder Channel(
        string id, JsonObject channel, string? index, bool json, Action<DiscordPackageChannelBuilder> messages)
    {
        var rows = new List<(string, DateTimeOffset, string, string)>();
        messages(new DiscordPackageChannelBuilder(rows));
        _channels.Add((id, channel, index, json, rows));

        return this;
    }

    /// <summary>Writes the package into <paramref name="root"/> and returns it.</summary>
    public string Write(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var options = new JsonSerializerOptions { WriteIndented = true };

        if (_owner is not null)
        {
            var account = Path.Combine(root, "account");
            Directory.CreateDirectory(account);

            File.WriteAllText(Path.Combine(account, "user.json"), new JsonObject
            {
                ["id"] = _owner.Id,
                ["username"] = _owner.Username,
                ["discriminator"] = "0",
                ["global_name"] = _owner.GlobalName,
            }.ToJsonString(options));
        }

        var messages = Path.Combine(root, "messages");
        Directory.CreateDirectory(messages);

        var index = new JsonObject();

        foreach (var (id, channel, name, json, rows) in _channels)
        {
            index[id] = name;

            var folder = Path.Combine(messages, $"c{id}");
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, "channel.json"), channel.ToJsonString(options));

            if (json)
            {
                var array = new JsonArray();

                foreach (var (messageId, at, contents, attachments) in rows)
                {
                    array.Add(new JsonObject
                    {
                        ["ID"] = long.Parse(messageId, CultureInfo.InvariantCulture),
                        ["Timestamp"] = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        ["Contents"] = contents,
                        ["Attachments"] = attachments,
                    });
                }

                File.WriteAllText(Path.Combine(folder, "messages.json"), array.ToJsonString(options));
            }
            else
            {
                var csv = new StringBuilder("ID,Timestamp,Contents,Attachments\n");

                foreach (var (messageId, at, contents, attachments) in rows)
                {
                    csv.Append(messageId).Append(',')
                        .Append(at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture)).Append(',')
                        .Append(Quote(contents)).Append(',')
                        .Append(Quote(attachments)).Append('\n');
                }

                File.WriteAllText(Path.Combine(folder, "messages.csv"), csv.ToString(), new UTF8Encoding(false));
            }
        }

        File.WriteAllText(Path.Combine(messages, "index.json"), index.ToJsonString(options));

        return root;
    }

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
}

/// <summary>The owner's messages in one package channel.</summary>
public sealed class DiscordPackageChannelBuilder(List<(string Id, DateTimeOffset At, string Contents, string Attachments)> rows)
{
    /// <param name="attachments">Space-separated URLs, as the package writes them.</param>
    public DiscordPackageChannelBuilder Message(string id, DateTimeOffset at, string contents, string attachments = "")
    {
        rows.Add((id, at, contents, attachments));

        return this;
    }
}

/// <summary>
/// Builds one DiscordChatExporter JSON file, as a real file on disk.
/// </summary>
public sealed class DiscordChatExporterBuilder
{
    private readonly JsonObject _guild;
    private readonly JsonObject _channel;
    private readonly JsonArray _messages = [];
    private readonly List<(string Name, int Seed)> _files = [];

    private DiscordChatExporterBuilder(JsonObject guild, JsonObject channel)
    {
        _guild = guild;
        _channel = channel;
    }

    public static DiscordChatExporterBuilder Dm(string channelId, string otherUsername) =>
        new(new JsonObject { ["id"] = "0", ["name"] = "Direct Messages" },
            new JsonObject { ["id"] = channelId, ["type"] = "DirectTextChat", ["category"] = "Private", ["name"] = otherUsername });

    public static DiscordChatExporterBuilder Group(string channelId, string name) =>
        new(new JsonObject { ["id"] = "0", ["name"] = "Direct Messages" },
            new JsonObject { ["id"] = channelId, ["type"] = "DirectGroupTextChat", ["category"] = "Group", ["name"] = name });

    public static DiscordChatExporterBuilder Guild(string guildName, string channelId, string channelName, string type = "GuildTextChat") =>
        new(new JsonObject { ["id"] = "900000000000000001", ["name"] = guildName },
            new JsonObject { ["id"] = channelId, ["type"] = type, ["category"] = "Text Channels", ["name"] = channelName });

    public string FileName => $"{_guild["name"]} - {_channel["name"]} [{_channel["id"]}].json";

    public DiscordChatExporterBuilder Message(
        string id, DateTimeOffset at, DiscordUser author, string content,
        Action<DiscordChatExporterMessageBuilder>? more = null, string type = "Default")
    {
        ArgumentNullException.ThrowIfNull(author);

        var node = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["timestamp"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["timestampEdited"] = null,
            ["callEndedTimestamp"] = null,
            ["isPinned"] = false,
            ["content"] = content,
            ["author"] = Author(author),
            ["attachments"] = new JsonArray(),
            ["embeds"] = new JsonArray(),
            ["stickers"] = new JsonArray(),
            ["reactions"] = new JsonArray(),
            ["mentions"] = new JsonArray(),
        };

        more?.Invoke(new DiscordChatExporterMessageBuilder(node, _files, FileName + "_Files"));
        _messages.Add(node);

        return this;
    }

    internal static JsonObject Author(DiscordUser user) => new()
    {
        ["id"] = user.Id,
        ["name"] = user.Username,
        ["discriminator"] = "0000",
        ["nickname"] = user.GlobalName ?? user.Username,
        ["isBot"] = false,
    };

    /// <summary>Writes the file, and any downloaded media beside it, into <paramref name="folder"/>.</summary>
    public string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, FileName), new JsonObject
        {
            ["guild"] = _guild.DeepClone(),
            ["channel"] = _channel.DeepClone(),
            ["dateRange"] = new JsonObject { ["after"] = null, ["before"] = null },
            ["exportedAt"] = "2021-03-20T10:00:00.000+00:00",
            ["messages"] = _messages.DeepClone(),
            ["messageCount"] = _messages.Count,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        foreach (var (name, seed) in _files)
        {
            SyntheticMedia.Write(folder, $"{FileName}_Files/{name}", seed);
        }

        return folder;
    }
}

/// <summary>What else one exported message carries.</summary>
public sealed class DiscordChatExporterMessageBuilder(JsonObject message, List<(string Name, int Seed)> files, string mediaFolder)
{
    /// <param name="seed">Downloaded beside the export when given, as <c>--media</c> does; a CDN link when null.</param>
    public DiscordChatExporterMessageBuilder Attachment(string fileName, int? seed)
    {
        string url;

        if (seed is { } s)
        {
            files.Add((fileName, s));
            url = Uri.EscapeDataString(mediaFolder) + "/" + Uri.EscapeDataString(fileName);
        }
        else
        {
            url = $"https://cdn.discordapp.com/attachments/1/2/{fileName}";
        }

        ((JsonArray)message["attachments"]!).Add(new JsonObject
        {
            ["id"] = "1",
            ["url"] = url,
            ["fileName"] = fileName,
            ["fileSizeBytes"] = 2048,
        });

        return this;
    }

    public DiscordChatExporterMessageBuilder Reaction(string emoji, int count, params DiscordUser[] users)
    {
        ((JsonArray)message["reactions"]!).Add(new JsonObject
        {
            ["emoji"] = new JsonObject { ["id"] = string.Empty, ["name"] = emoji, ["isAnimated"] = false },
            ["count"] = count,
            ["users"] = new JsonArray([.. users.Select(u => (JsonNode)DiscordChatExporterBuilder.Author(u))]),
        });

        return this;
    }

    public DiscordChatExporterMessageBuilder Reply(string messageId, string? channelId = null)
    {
        message["type"] = "Reply";
        message["reference"] = new JsonObject { ["messageId"] = messageId, ["channelId"] = channelId, ["guildId"] = null };

        return this;
    }

    public DiscordChatExporterMessageBuilder Edited(DateTimeOffset at)
    {
        message["timestampEdited"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

        return this;
    }
}
