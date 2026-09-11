using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Archive.Import.Sms;

namespace Archive.Import.GoogleVoice;

/// <summary>
/// Google Voice, as Takeout exports it.
/// </summary>
/// <remarks>
/// <para>
/// <c>Voice/Calls</c> holds one HTML file per text conversation and per call, named
/// <c>&lt;who&gt; - &lt;kind&gt; - &lt;time&gt;.html</c> — <c>Text</c>, <c>Received</c>,
/// <c>Placed</c>, <c>Missed</c>, <c>Voicemail</c>, <c>Recorded</c> — with pictures and voicemail
/// audio beside them. The markup is hCard and hAtom: a message is <c>div.message</c>, its time an
/// <c>abbr.dt</c> whose title is ISO 8601 with the offset the phone was in, its sender a
/// <c>cite.vcard</c> around a <c>tel:</c> link.
/// </para>
/// <para>
/// What makes it worth reading: every time is absolute and carries its own offset, and every person
/// is a phone number — the SMS reader's key, formatted the same way.
/// </para>
/// <para>
/// Traps:
/// </para>
/// <list type="bullet">
///   <item><b>One person's texts are spread over many files.</b> They are one conversation, keyed by
///   the other person's number rather than by file.</item>
///   <item><b>A group's file has no kind in its name</b> — <c>Group Conversation - &lt;time&gt;.html</c>
///   — and its people are listed in a <c>div.participants</c> above the messages.</item>
///   <item><b>Your own messages are signed "Me"</b>, around a <c>tel:</c> link to your own Voice
///   number — which is how the owner is found, and why nothing has to be asked.</item>
///   <item><b>There are no message ids.</b> A uid is the conversation, the second, and a hash of
///   sender and text, counted for repeats within a file.</item>
///   <item><b>Calls are files too.</b> They become service messages in the conversation with that
///   number, and a voicemail becomes a message carrying its transcript and its audio.</item>
///   <item>A file whose kind is none of the six stops the import rather than being passed over.</item>
/// </list>
/// </remarks>
public sealed partial class GoogleVoiceImporter : IPlatformImporter
{
    public const string PlatformId = "googlevoice";

    private const char Separator = (char)31;

    /// <summary><c>&lt;who&gt; - &lt;kind&gt; - &lt;time&gt;</c>, or a group's <c>Group Conversation - &lt;time&gt;</c>, which has no kind.</summary>
    [GeneratedRegex(@"^(?:(?<who>.+) - (?<kind>[A-Za-z]+)|(?<who>Group Conversation)) - (?<ts>\d{4}-\d{2}-\d{2}T\d{2}_\d{2}_\d{2}Z)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    [GeneratedRegex(@"href=""tel:(?<number>[^""]+)""[^>]*>\s*<abbr class=""fn""[^>]*>Me</abbr>", RegexOptions.CultureInvariant)]
    private static partial Regex OwnLink();

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "Text", "Received", "Placed", "Missed", "Voicemail", "Recorded",
    };

    public string Platform => PlatformId;

    public string DisplayName => "Google Voice";

    public ImportDetection Detect(string path)
    {
        var calls = CallsFolder(path);

        if (calls is null)
        {
            return ImportDetection.No;
        }

        var files = Files(calls);
        var owner = OwnNumber(files.Where(f => KindOf(f) == "Text").Take(20));

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: owner,
            AccountName: owner,
            FileCount: files.Length,
            Note: "Google Voice has no message ids: a message is recognized by its time, sender and "
                + "text, so one edited between two Takeouts would arrive as a second one.",
            AccountIdIsGuess: owner is null);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var calls = CallsFolder(path)
            ?? throw new InvalidDataException(
                $"'{path}' has no Voice/Calls folder. Take Google Voice from Takeout and point at it or its parent.");

        var files = Files(calls);

        foreach (var file in files)
        {
            if (!Kinds.Contains(KindOf(file)))
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(file)}' is a Google Voice file of kind '{KindOf(file)}', which this reader does not know.");
            }
        }

        var ownNumber = options?.OwnerAccountId is { Length: > 0 } stated
            ? SmsBackupImporter.Normalize(stated)
            : OwnNumber(files.Where(f => KindOf(f) == "Text"));

        var owner = ownNumber is null
            ? new NormalizedIdentity(PlatformId, null, null, "You (number not identified)", IsSynthetic: true)
            : new NormalizedIdentity(PlatformId, ownNumber, ownNumber, "You", IsSynthetic: false);

        sink.OnOwner(owner);

        var parser = new HtmlParser();
        var announced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var document = parser.ParseDocument(File.ReadAllText(file, Encoding.UTF8));

            var read = KindOf(file) == "Text"
                ? ReadText(path, calls, file, document, owner)
                : ReadCall(path, calls, file, document, owner);

            foreach (var (thread, message) in read)
            {
                if (announced.Add(thread.SourceThreadId))
                {
                    sink.OnThread(thread);
                }

                sink.OnMessage(thread, message);
            }
        }
    }

    /// <summary><c>Voice/Calls</c>, pointed at directly or from the Voice folder, the Takeout folder or its parent.</summary>
    private static string? CallsFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     path,
                     Path.Combine(path, "Calls"),
                     Path.Combine(path, "Voice", "Calls"),
                     Path.Combine(path, "Takeout", "Voice", "Calls"),
                 })
        {
            if (Directory.Exists(candidate) && Files(candidate).Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] Files(string calls) =>
        [.. Directory.EnumerateFiles(calls, "*.html", SearchOption.TopDirectoryOnly)
            .Where(f => FileName().IsMatch(Path.GetFileNameWithoutExtension(f)))
            .Order(StringComparer.Ordinal)];

    /// <summary>The file's kind; a group conversation's name carries none, and it is texts.</summary>
    private static string KindOf(string file) =>
        FileName().Match(Path.GetFileNameWithoutExtension(file)).Groups["kind"] is { Success: true } kind ? kind.Value : "Text";

    private static string WhoOf(string file) => FileName().Match(Path.GetFileNameWithoutExtension(file)).Groups["who"].Value;

    /// <summary>The number your own messages are signed with, by majority.</summary>
    private static string? OwnNumber(IEnumerable<string> textFiles)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in textFiles)
        {
            foreach (Match match in OwnLink().Matches(File.ReadAllText(file, Encoding.UTF8)))
            {
                var number = SmsBackupImporter.Normalize(match.Groups["number"].Value);
                counts[number] = counts.GetValueOrDefault(number) + 1;
            }
        }

        return counts.Count == 0 ? null : counts.MaxBy(p => p.Value).Key;
    }

    private static NormalizedIdentity Person(string number, string? name)
    {
        var normalized = SmsBackupImporter.Normalize(number);

        return new NormalizedIdentity(PlatformId, normalized, number, string.IsNullOrWhiteSpace(name) ? normalized : name, IsSynthetic: false);
    }

    /// <summary>A <c>tel:</c> link's number and the name beside it; "Me" is the owner.</summary>
    private static NormalizedIdentity? Who(IElement? scope, NormalizedIdentity owner)
    {
        var link = scope?.QuerySelector("a.tel");
        var href = link?.GetAttribute("href");

        if (href is null || !href.StartsWith("tel:", StringComparison.Ordinal))
        {
            return null;
        }

        var name = link!.QuerySelector(".fn")?.TextContent.Trim();

        if (name == "Me")
        {
            return owner;
        }

        var person = Person(href[4..], name);

        return person.SourceIdentityId == owner.SourceIdentityId ? owner : person;
    }

    private IEnumerable<(NormalizedThread Thread, NormalizedMessage Message)> ReadText(
        string exportRoot, string calls, string file, IDocument document, NormalizedIdentity owner)
    {
        var messages = document.QuerySelectorAll("div.message").ToArray();

        var participants = document.QuerySelectorAll("div.participants cite")
            .Select(c => Who(c, owner))
            .OfType<NormalizedIdentity>()
            .Where(p => p != owner)
            .DistinctBy(p => p.SourceIdentityId)
            .ToList();

        var senders = messages
            .Select(m => Who(m.QuerySelector("cite.sender"), owner))
            .OfType<NormalizedIdentity>()
            .Where(p => p != owner)
            .DistinctBy(p => p.SourceIdentityId)
            .ToList();

        NormalizedThread thread;

        if (participants.Count > 1 || WhoOf(file) == "Group Conversation")
        {
            var people = participants.Concat(senders).DistinctBy(p => p.SourceIdentityId).ToArray();

            thread = new NormalizedThread(
                "group/" + string.Join(",", people.Select(p => p.SourceIdentityId).Order(StringComparer.Ordinal)),
                "group",
                string.Join(", ", people.Select(p => p.DisplayName)),
                people);
        }
        else if ((participants.FirstOrDefault() ?? senders.FirstOrDefault()) is { } other)
        {
            thread = Direct(other);
        }
        else
        {
            // Only your own side survived, and the file name is all that says who it was with.
            var who = WhoOf(file);
            thread = new NormalizedThread($"text/name:{who}", "dm", who, []);
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var folder = Path.GetDirectoryName(file)!;

        foreach (var message in messages)
        {
            var at = Time(message.QuerySelector("abbr.dt"), file);
            var sender = Who(message.QuerySelector("cite.sender"), owner)
                ?? throw new InvalidDataException($"A message in '{Path.GetFileName(file)}' has no sender.");

            var text = Quote(message.QuerySelector("q"));
            var media = Attachments(exportRoot, folder, message);

            yield return (thread, Build(thread, at, sender, text, media, "message", null, seen, message.OuterHtml));
        }
    }

    private IEnumerable<(NormalizedThread Thread, NormalizedMessage Message)> ReadCall(
        string exportRoot, string calls, string file, IDocument document, NormalizedIdentity owner)
    {
        var kind = KindOf(file);
        var audio = document.QuerySelector("div.haudio")
            ?? throw new InvalidDataException($"'{Path.GetFileName(file)}' has no call in it.");

        var other = Who(audio, owner);

        var thread = other is null || other == owner
            ? new NormalizedThread($"text/name:{WhoOf(file)}", "dm", WhoOf(file), [])
            : Direct(other);

        var at = Time(audio.QuerySelector("abbr.published"), file);
        var sender = kind == "Placed" ? owner : other ?? owner;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        if (kind == "Voicemail")
        {
            var transcript = audio.QuerySelector(".full-text")?.TextContent.Trim() ?? string.Empty;
            var media = Attachments(exportRoot, Path.GetDirectoryName(file)!, audio);
            var duration = Duration(audio.QuerySelector("abbr.duration")?.GetAttribute("title"));

            media = [.. media.Select(m => m with { MediaKind = "voice", DurationSeconds = duration })];

            yield return (thread, Build(thread, at, sender, transcript, media, "message", null, seen, audio.OuterHtml));
            yield break;
        }

        var service = kind == "Missed" ? "missed_call" : "phone_call";

        yield return (thread, Build(thread, at, sender, string.Empty, [], "service", service, seen, audio.OuterHtml));
    }

    private static NormalizedThread Direct(NormalizedIdentity other) =>
        new($"text/{other.SourceIdentityId}", "dm", other.DisplayName, [other]);

    private static NormalizedMessage Build(
        NormalizedThread thread, DateTimeOffset at, NormalizedIdentity sender, string text, List<NormalizedMedia> media,
        string kind, string? service, Dictionary<string, int> seen, string raw)
    {
        var identity = $"{sender.SourceIdentityId ?? sender.DisplayName}{Separator}{service}{Separator}{text}";
        var key = $"{at.ToUnixTimeSeconds()}/{Hash(identity)[..12]}";
        var occurrence = seen.GetValueOrDefault(key);
        seen[key] = occurrence + 1;

        return new NormalizedMessage
        {
            Uid = $"gv/{thread.SourceThreadId}/{key}/{occurrence}",
            SourceThreadId = thread.SourceThreadId,
            Kind = kind,
            Sender = sender,
            ServiceAction = service,
            SentAtUtc = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            SentAtUnix = at.ToUnixTimeSeconds(),
            TzOffsetMinutes = (long)at.Offset.TotalMinutes,
            Plaintext = text,
            ContentHash = Hash($"{identity}{Separator}{string.Join(Separator, media.Select(m => m.ExportPath))}"),
            RawJson = raw,
            Media = media,
        };
    }

    /// <summary>The ISO 8601 title of an <c>abbr</c>, offset and all.</summary>
    private static DateTimeOffset Time(IElement? abbr, string file)
    {
        var title = abbr?.GetAttribute("title");

        return title is not null &&
               DateTimeOffset.TryParse(title, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : throw new InvalidDataException($"A message in '{Path.GetFileName(file)}' has no time Google Voice wrote.");
    }

    /// <summary>A message's words, with its line breaks.</summary>
    private static string Quote(IElement? q)
    {
        if (q is null)
        {
            return string.Empty;
        }

        foreach (var br in q.QuerySelectorAll("br").ToArray())
        {
            br.Replace(q.Owner!.CreateTextNode("\n"));
        }

        return q.TextContent.Trim();
    }

    /// <summary>Pictures, videos, contact cards and audio, found beside the file.</summary>
    private static List<NormalizedMedia> Attachments(string exportRoot, string folder, IElement scope)
    {
        var media = new List<NormalizedMedia>();

        foreach (var element in scope.QuerySelectorAll("img[src], a.video[href], a.vcard[href], audio[src]"))
        {
            var reference = element.GetAttribute("src") ?? element.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith("tel:", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Uri.UnescapeDataString(reference);
            var relative = Path.GetRelativePath(exportRoot, Path.Combine(folder, name)).Replace('\\', '/');

            var kind = element.LocalName switch
            {
                "img" => "photo",
                "audio" => "voice",
                _ when element.ClassList.Contains("video") => "video",
                _ => "file",
            };

            media.Add(new NormalizedMedia(
                relative, kind, Path.GetFileName(name), Mime: null, MissingReason: null,
                StickerEmoji: null, Width: null, Height: null, DurationSeconds: null));
        }

        return media;
    }

    /// <summary>An ISO 8601 duration such as <c>PT1M5S</c>, in seconds.</summary>
    private static long? Duration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return (long)XmlConvert.ToTimeSpan(value).TotalSeconds;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
}
