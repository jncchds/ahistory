using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archive.Import.Meta;

namespace Archive.Import.Synthetic;

/// <summary>Where a Meta download puts its <c>messages</c> folder.</summary>
public enum MetaLayout
{
    /// <summary>Under <c>your_facebook_activity</c> or <c>your_instagram_activity</c>.</summary>
    Current,

    /// <summary>At the root of the download, as older downloads did.</summary>
    Legacy,
}

/// <summary>
/// Builds a Facebook or Instagram "download your information" folder, as real files on disk.
/// </summary>
/// <remarks>
/// Every string is written double-encoded, each UTF-8 byte as its own <c>\u00XX</c> escape, because
/// that is what Meta does — a builder that wrote clean UTF-8 would test a format nobody has.
/// </remarks>
public sealed class MetaExportBuilder
{
    private readonly MetaProduct _product;
    private readonly string? _owner;
    private readonly MetaLayout _layout;
    private readonly List<ThreadSpec> _threads = [];

    private sealed record ThreadSpec(
        string Folder, string? Title, IReadOnlyList<string> Participants, string Box, int PerFile,
        string? ThreadType, List<(DateTimeOffset At, JsonObject Node)> Messages, List<(string Uri, int Seed)> Files);

    private MetaExportBuilder(MetaProduct product, string? owner, MetaLayout layout)
    {
        _product = product;
        _owner = owner;
        _layout = layout;
    }

    /// <param name="owner">Written to the profile file; null leaves it out, so the owner has to be inferred.</param>
    public static MetaExportBuilder Facebook(string? owner, MetaLayout layout = MetaLayout.Current) =>
        new(MetaProduct.Facebook, owner, layout);

    public static MetaExportBuilder Instagram(string? owner, MetaLayout layout = MetaLayout.Current) =>
        new(MetaProduct.Instagram, owner, layout);

    /// <summary>The path of <c>messages</c> relative to the download root, with a trailing slash.</summary>
    public string MessagesPrefix => _layout == MetaLayout.Legacy
        ? "messages/"
        : _product == MetaProduct.Facebook ? "your_facebook_activity/messages/" : "your_instagram_activity/messages/";

    /// <param name="folder">The thread's folder name, e.g. <c>samruiz_1234567890</c>.</param>
    /// <param name="box"><c>inbox</c>, <c>archived_threads</c> and the rest.</param>
    /// <param name="perFile">Messages per <c>message_N.json</c>, newest first as Meta splits them.</param>
    public MetaExportBuilder Thread(
        string folder,
        string? title,
        IReadOnlyList<string> participants,
        Action<MetaThreadBuilder> messages,
        string box = "inbox",
        int perFile = 10_000,
        string? threadType = null)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(messages);

        var spec = new ThreadSpec(folder, title, participants, box, perFile, threadType, [], []);
        messages(new MetaThreadBuilder(spec.Messages, spec.Files, $"{MessagesPrefix}{box}/{folder}/"));
        _threads.Add(spec);

        return this;
    }

    /// <summary>Writes the download into <paramref name="root"/> and returns it.</summary>
    public string Write(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);

        WriteProfile(root);

        foreach (var thread in _threads)
        {
            var folder = Path.Combine(root, MessagesPrefix.TrimEnd('/'), thread.Box, thread.Folder);
            Directory.CreateDirectory(folder);

            var newestFirst = thread.Messages.OrderByDescending(m => m.At).Select(m => m.Node).ToArray();
            var parts = newestFirst.Chunk(thread.PerFile).ToArray();

            if (parts.Length == 0)
            {
                parts = [[]];
            }

            for (var i = 0; i < parts.Length; i++)
            {
                var json = new JsonObject
                {
                    ["participants"] = new JsonArray([.. thread.Participants.Select(p => (JsonNode)new JsonObject { ["name"] = Mangle(p) })]),
                    ["messages"] = new JsonArray([.. parts[i].Select(n => n.DeepClone())]),
                    ["is_still_participant"] = true,
                    ["thread_path"] = $"{thread.Box}/{thread.Folder}",
                };

                if (thread.Title is not null)
                {
                    json["title"] = Mangle(thread.Title);
                }

                if (thread.ThreadType is not null)
                {
                    json["thread_type"] = thread.ThreadType;
                }

                File.WriteAllText(Path.Combine(folder, $"message_{i + 1}.json"), json.ToJsonString(Options));
            }

            foreach (var (uri, seed) in thread.Files)
            {
                SyntheticMedia.Write(root, uri, seed);
            }
        }

        return root;
    }

    private void WriteProfile(string root)
    {
        if (_product == MetaProduct.Facebook)
        {
            if (_owner is null)
            {
                // Still marked as Facebook's, by the activity folder or by the profile folder.
                if (_layout == MetaLayout.Legacy)
                {
                    Directory.CreateDirectory(Path.Combine(root, "profile_information"));
                }

                return;
            }

            var profile = _layout == MetaLayout.Legacy
                ? Path.Combine(root, "profile_information", "profile_information.json")
                : Path.Combine(root, "personal_information", "profile_information", "profile_information.json");

            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);

            var key = _layout == MetaLayout.Legacy ? "profile" : "profile_v2";

            File.WriteAllText(profile, new JsonObject
            {
                [key] = new JsonObject { ["name"] = new JsonObject { ["full_name"] = Mangle(_owner) } },
            }.ToJsonString(Options));

            return;
        }

        var personal = Path.Combine(root, "personal_information", "personal_information", "personal_information.json");
        Directory.CreateDirectory(Path.GetDirectoryName(personal)!);

        var map = new JsonObject();

        if (_owner is not null)
        {
            map["Name"] = new JsonObject { ["value"] = Mangle(_owner) };
        }

        File.WriteAllText(personal, new JsonObject
        {
            ["profile_user"] = new JsonArray(new JsonObject { ["string_map_data"] = map }),
        }.ToJsonString(Options));
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Meta's double encoding: each UTF-8 byte becomes a character of its own.
    /// </summary>
    /// <remarks>
    /// The serializer then writes every one of those above 0x7F as <c>\u00XX</c>, which is byte
    /// for byte what a real download contains.
    /// </remarks>
    public static string Mangle(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));
}

/// <summary>The messages of one thread.</summary>
public sealed class MetaThreadBuilder(
    List<(DateTimeOffset At, JsonObject Node)> messages, List<(string Uri, int Seed)> files, string threadPrefix)
{
    public MetaThreadBuilder Message(
        string sender, DateTimeOffset at, string text, Action<MetaMessageBuilder>? more = null)
    {
        var node = Node(sender, at);
        node["content"] = MetaExportBuilder.Mangle(text);
        node["type"] = "Generic";

        more?.Invoke(new MetaMessageBuilder(node, files, threadPrefix));
        messages.Add((at, node));

        return this;
    }

    public MetaThreadBuilder Call(string sender, DateTimeOffset at, int seconds)
    {
        var node = Node(sender, at);
        node["type"] = "Call";
        node["call_duration"] = seconds;

        messages.Add((at, node));

        return this;
    }

    /// <summary>A message of whatever type is given, for testing the refusal of one nobody knows.</summary>
    public MetaThreadBuilder OfType(string sender, DateTimeOffset at, string type)
    {
        var node = Node(sender, at);
        node["type"] = type;

        messages.Add((at, node));

        return this;
    }

    private static JsonObject Node(string sender, DateTimeOffset at) => new()
    {
        ["sender_name"] = MetaExportBuilder.Mangle(sender),
        ["timestamp_ms"] = at.ToUnixTimeMilliseconds(),
        ["is_geoblocked_for_viewer"] = false,
    };
}

/// <summary>What else one message carries.</summary>
public sealed class MetaMessageBuilder(JsonObject message, List<(string Uri, int Seed)> files, string threadPrefix)
{
    /// <summary>A photo, written into the thread's own photos folder with its uri from the download root.</summary>
    public MetaMessageBuilder Photo(string fileName, int seed)
    {
        var uri = $"{threadPrefix}photos/{fileName}";

        if (message["photos"] is not JsonArray photos)
        {
            photos = [];
            message["photos"] = photos;
        }

        photos.Add(new JsonObject { ["uri"] = uri, ["creation_timestamp"] = 0 });
        files.Add((uri, seed));

        return this;
    }

    public MetaMessageBuilder Reaction(string emoji, string actor)
    {
        if (message["reactions"] is not JsonArray reactions)
        {
            reactions = [];
            message["reactions"] = reactions;
        }

        reactions.Add(new JsonObject
        {
            ["reaction"] = MetaExportBuilder.Mangle(emoji),
            ["actor"] = MetaExportBuilder.Mangle(actor),
        });

        return this;
    }

    public MetaMessageBuilder Share(string link)
    {
        message["share"] = new JsonObject { ["link"] = link };
        message["type"] = "Share";

        return this;
    }
}
