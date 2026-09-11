using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archive.Import.Synthetic;

/// <summary>One Google Chat account: a name and, unless deleted, an email address.</summary>
public sealed record GoogleChatUser(string Name, string? Email);

/// <summary>Which of Takeout's two English date orders to write.</summary>
public enum GoogleChatDateStyle
{
    /// <summary><c>Tuesday, March 14, 2021 at 10:41:03 PM UTC</c>.</summary>
    MonthFirst,

    /// <summary><c>Tuesday, 14 March 2021 at 22:41:03 UTC</c>.</summary>
    DayFirst,
}

/// <summary>
/// Builds a Google Takeout <c>Google Chat</c> folder, as real files on disk.
/// </summary>
/// <remarks>
/// See <see cref="TelegramExportBuilder"/> for why exports are built in code rather than committed.
/// </remarks>
public sealed class GoogleChatExportBuilder
{
    private readonly GoogleChatUser? _owner;
    private readonly GoogleChatDateStyle _style;
    private readonly List<(string Folder, JsonObject Info, JsonArray Messages, List<(string Name, int Seed)> Files)> _groups = [];

    private GoogleChatExportBuilder(GoogleChatUser? owner, GoogleChatDateStyle style)
    {
        _owner = owner;
        _style = style;
    }

    /// <param name="owner">Written to <c>Users/User 1/user_info.json</c>; null leaves the Users folder out.</param>
    public static GoogleChatExportBuilder New(
        GoogleChatUser? owner, GoogleChatDateStyle style = GoogleChatDateStyle.MonthFirst) => new(owner, style);

    public GoogleChatExportBuilder Dm(
        string id, IReadOnlyList<GoogleChatUser> members, Action<GoogleChatGroupBuilder> messages) =>
        Group($"DM {id}", name: null, members, messages);

    public GoogleChatExportBuilder Space(
        string id, string name, IReadOnlyList<GoogleChatUser> members, Action<GoogleChatGroupBuilder> messages) =>
        Group($"Space {id}", name, members, messages);

    private GoogleChatExportBuilder Group(
        string folder, string? name, IReadOnlyList<GoogleChatUser> members, Action<GoogleChatGroupBuilder> messages)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(messages);

        var info = new JsonObject();

        if (name is not null)
        {
            info["name"] = name;
        }

        info["members"] = new JsonArray([.. members.Select(m => (JsonNode)User(m))]);

        var array = new JsonArray();
        var files = new List<(string, int)>();
        messages(new GoogleChatGroupBuilder(array, files, _style));

        _groups.Add((folder, info, array, files));

        return this;
    }

    internal static JsonObject User(GoogleChatUser user)
    {
        var node = new JsonObject { ["name"] = user.Name };

        if (user.Email is not null)
        {
            node["email"] = user.Email;
        }

        node["user_type"] = "Human";

        return node;
    }

    /// <summary>Writes <c>Google Chat</c> inside <paramref name="folder"/> and returns the folder.</summary>
    public string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = Path.Combine(folder, "Google Chat");
        var options = new JsonSerializerOptions { WriteIndented = true };

        if (_owner is not null)
        {
            var users = Path.Combine(root, "Users", "User 1");
            Directory.CreateDirectory(users);

            File.WriteAllText(
                Path.Combine(users, "user_info.json"),
                new JsonObject { ["user"] = User(_owner) }.ToJsonString(options));
        }

        foreach (var (name, info, messages, files) in _groups)
        {
            var group = Path.Combine(root, "Groups", name);
            Directory.CreateDirectory(group);

            File.WriteAllText(Path.Combine(group, "group_info.json"), info.ToJsonString(options));
            File.WriteAllText(
                Path.Combine(group, "messages.json"),
                new JsonObject { ["messages"] = messages.DeepClone() }.ToJsonString(options));

            foreach (var (file, seed) in files)
            {
                SyntheticMedia.Write(group, file, seed);
            }
        }

        return folder;
    }
}

/// <summary>The messages of one conversation.</summary>
public sealed class GoogleChatGroupBuilder(
    JsonArray messages, List<(string Name, int Seed)> files, GoogleChatDateStyle style)
{
    /// <param name="id">The <c>message_id</c>; null writes a message from an export too old to carry one.</param>
    public GoogleChatGroupBuilder Message(
        string? id, DateTimeOffset at, GoogleChatUser creator, string text, Action<GoogleChatMessageBuilder>? more = null)
    {
        ArgumentNullException.ThrowIfNull(creator);

        var node = new JsonObject
        {
            ["creator"] = GoogleChatExportBuilder.User(creator),
            ["created_date"] = Date(at, style),
            ["text"] = text,
        };

        if (id is not null)
        {
            node["message_id"] = id;
        }

        more?.Invoke(new GoogleChatMessageBuilder(node, style));
        messages.Add(node);

        return this;
    }

    /// <summary>A message whose date is whatever string is given, for testing the refusal.</summary>
    public GoogleChatGroupBuilder MessageDated(string id, string createdDate, GoogleChatUser creator, string text)
    {
        messages.Add(new JsonObject
        {
            ["creator"] = GoogleChatExportBuilder.User(creator),
            ["created_date"] = createdDate,
            ["text"] = text,
            ["message_id"] = id,
        });

        return this;
    }

    /// <summary>Puts an attachment's bytes in the conversation's folder, as Takeout does.</summary>
    public GoogleChatGroupBuilder File(string exportName, int seed)
    {
        files.Add((exportName, seed));

        return this;
    }

    /// <summary>
    /// Takeout's English prose date.
    /// </summary>
    /// <remarks>
    /// The month-first form carries U+202F before AM/PM, as newer exports do, so a reader that
    /// only expects an ordinary space fails here rather than on someone's real archive.
    /// </remarks>
    internal static string Date(DateTimeOffset at, GoogleChatDateStyle style)
    {
        var utc = at.ToUniversalTime();
        var culture = CultureInfo.InvariantCulture;

        return style == GoogleChatDateStyle.MonthFirst
            ? utc.ToString("dddd, MMMM d, yyyy 'at' h:mm:ss", culture) + (char)0x202F + utc.ToString("tt", culture) + " UTC"
            : utc.ToString("dddd, d MMMM yyyy 'at' HH:mm:ss 'UTC'", culture);
    }
}

/// <summary>What else one message carries.</summary>
public sealed class GoogleChatMessageBuilder(JsonObject message, GoogleChatDateStyle style)
{
    public GoogleChatMessageBuilder Attachment(string originalName, string? exportName)
    {
        if (message["attached_files"] is not JsonArray files)
        {
            files = [];
            message["attached_files"] = files;
        }

        var node = new JsonObject { ["original_name"] = originalName };

        if (exportName is not null)
        {
            node["export_name"] = exportName;
        }

        files.Add(node);

        return this;
    }

    public GoogleChatMessageBuilder Reaction(string emoji, params string[] reactorEmails)
    {
        if (message["reactions"] is not JsonArray reactions)
        {
            reactions = [];
            message["reactions"] = reactions;
        }

        reactions.Add(new JsonObject
        {
            ["emoji"] = new JsonObject { ["unicode"] = emoji },
            ["reactor_emails"] = new JsonArray([.. reactorEmails.Select(e => (JsonNode)e)]),
        });

        return this;
    }

    public GoogleChatMessageBuilder Edited(DateTimeOffset at)
    {
        message["updated_date"] = GoogleChatGroupBuilder.Date(at, style);

        return this;
    }

    public GoogleChatMessageBuilder Quotes(string messageId)
    {
        message["quoted_message_metadata"] = new JsonObject { ["message_id"] = messageId };

        return this;
    }
}
