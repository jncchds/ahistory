using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Archive.Import.Vk;

/// <summary>
/// VKontakte's "download your data" archive.
/// </summary>
/// <remarks>
/// <para>
/// HTML rather than JSON: <c>messages/&lt;peer id&gt;/messages0.html</c>, paginated in
/// fifties. Each message is an element with class <c>message</c> and a numeric <c>data-id</c>;
/// its <c>message__header</c> holds a link to the sender and a Russian date, and attachments live
/// in a <c>kludges</c> block.
/// </para>
/// <para>
/// The traps here are its own:
/// </para>
/// <list type="bullet">
///   <item><b>Your own messages have no sender link.</b> VK omits it rather than linking to you,
///   so an absent anchor means "me" — and reading it as "unknown" would put half of every
///   conversation on the wrong side.</item>
///   <item><b>Dates are Russian text</b> — <c>1 янв 2020 в 12:34:56</c>. The month names are
///   mapped explicitly rather than through a culture, because this app runs with invariant
///   globalization, where <c>ru-RU</c> silently collapses to the invariant culture and every date
///   fails to parse.</item>
///   <item><b>An edited message adds a <c>message-edited</c> span inside the header</b>, whose
///   text lands between the name and the date. It is removed before the date is read.</item>
///   <item><b>Negative peer ids are groups and chats</b>, positive ones are people.</item>
/// </list>
/// <para>
/// Structure confirmed against
/// <see href="https://github.com/Darkar25/VkArchiveParser">Darkar25/VkArchiveParser</see>, which
/// reads the same archives. Anything this reader does not recognize stops the import rather than
/// being skipped.
/// </para>
/// </remarks>
public sealed class VkImporter : IPlatformImporter
{
    public const string PlatformId = "vk";

    /// <summary>
    /// Russian month abbreviations as VK writes them.
    /// </summary>
    /// <remarks>
    /// Both nominative and genitive forms, because May appears as <c>май</c> in some archives and
    /// <c>мая</c> in others, and one of them would otherwise fail on a date nobody was watching.
    /// </remarks>
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["янв"] = 1, ["фев"] = 2, ["мар"] = 3, ["апр"] = 4,
        ["май"] = 5, ["мая"] = 5, ["июн"] = 6, ["июл"] = 7, ["авг"] = 8,
        ["сен"] = 9, ["окт"] = 10, ["ноя"] = 11, ["дек"] = 12,
    };

    /// <summary><c>1 янв 2020 в 12:34:56</c>, with the seconds optional.</summary>
    private static readonly Regex DatePattern = new(
        @"(?<day>\d{1,2})\s+(?<month>[^\s\d]+)\s+(?<year>\d{4})\s+в\s+(?<hour>\d{1,2}):(?<minute>\d{2})(:(?<second>\d{2}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PageNumber = new(@"messages(\d+)\.html", RegexOptions.Compiled);

    private static readonly Regex ProfileId = new(@"(?<kind>id|public|club)(?<id>\d+)", RegexOptions.Compiled);

    public string Platform => PlatformId;

    public string DisplayName => "VKontakte";

    public ImportDetection Detect(string path)
    {
        var root = MessagesRoot(path);

        if (root is null)
        {
            return ImportDetection.No;
        }

        var files = PageFiles(root);

        if (files.Length == 0)
        {
            return ImportDetection.No;
        }

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: null,
            AccountName: null,
            FileCount: files.Length,
            Note: "VK archives do not state which account they belong to. Messages with no sender "
                + "link are treated as yours, which is how VK marks them.");
    }

    public void Read(string path, IImportSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var root = MessagesRoot(path)
            ?? throw new InvalidDataException(
                $"'{path}' has no messages folder. Point at the folder VK's archive unpacked into.");

        // The account is never named in the archive, so it gets a fixed identity rather than an
        // invented one. Every message VK left unattributed is this person, which is what makes
        // the two sides of a conversation distinguishable at all.
        var owner = new NormalizedIdentity(PlatformId, "self", Handle: null, "You", IsSynthetic: false);
        sink.OnOwner(owner);

        var context = BrowsingContext.New(Configuration.Default);

        foreach (var peer in Directory.EnumerateDirectories(root).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            ReadConversation(context, peer, owner, sink);
        }
    }

    /// <summary>The <c>messages</c> folder, whether it was pointed at directly or via its parent.</summary>
    private static string? MessagesRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        if (Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))
                .Equals("messages", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var nested = Path.Combine(path, "messages");

        return Directory.Exists(nested) ? nested : null;
    }

    private static string[] PageFiles(string root) =>
        [.. Directory.EnumerateFiles(root, "messages*.html", SearchOption.AllDirectories)];

    private void ReadConversation(IBrowsingContext context, string peerFolder, NormalizedIdentity owner, IImportSink sink)
    {
        var peerId = Path.GetFileName(peerFolder);

        var pages = Directory.EnumerateFiles(peerFolder, "messages*.html")
            .OrderBy(PageOrder)
            .ToArray();

        if (pages.Length == 0)
        {
            return;
        }

        // Negative ids are groups and multi-person chats; positive ones are people.
        var kind = peerId.StartsWith('-') ? "group" : "dm";

        NormalizedThread? thread = null;

        foreach (var page in pages)
        {
            var document = Parse(context, page);

            // VK puts the conversation's name in the breadcrumb at the top of every page.
            thread ??= Announce(sink, peerId, kind, Title(document));

            foreach (var element in document.GetElementsByClassName("message"))
            {
                sink.OnMessage(thread, ReadMessage(element, peerId, page, owner));
            }
        }
    }

    private static NormalizedThread Announce(IImportSink sink, string peerId, string kind, string? title)
    {
        var thread = new NormalizedThread(peerId, kind, title);
        sink.OnThread(thread);

        return thread;
    }

    /// <summary>Pages are numbered by the message they start at: 0, 50, 100.</summary>
    private static int PageOrder(string file)
    {
        var match = PageNumber.Match(Path.GetFileName(file));

        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Parses a page, letting the document declare its own encoding.
    /// </summary>
    /// <remarks>
    /// Older VK archives are windows-1251 and newer ones UTF-8, both declared in a meta tag.
    /// Handing the raw stream to the parser lets it honour that; reading the file as a string
    /// first would decode it as UTF-8 and turn every Cyrillic name into mojibake.
    /// </remarks>
    private static IDocument Parse(IBrowsingContext context, string file)
    {
        using var stream = File.OpenRead(file);

        return context.OpenAsync(request => request.Content(stream).Address("file:///" + file))
            .GetAwaiter().GetResult();
    }

    private static string? Title(IDocument document)
    {
        var crumbs = document.GetElementsByClassName("ui_crumb");

        return crumbs.Length > 0 ? crumbs[^1].TextContent.Trim() : null;
    }

    private NormalizedMessage ReadMessage(IElement element, string peerId, string page, NormalizedIdentity owner)
    {
        var id = element.GetAttribute("data-id");

        if (string.IsNullOrWhiteSpace(id))
        {
            // Without VK's own id there is no stable key, and a generated one would make the
            // import non-idempotent — the same archive would duplicate itself on every run.
            throw new InvalidDataException(
                $"A message in '{page}' has no data-id. This is not the archive layout this reader knows.");
        }

        var header = element.GetElementsByClassName("message__header").FirstOrDefault()
            ?? throw new InvalidDataException($"A message in '{page}' has no message__header.");

        var link = header.QuerySelector<IHtmlAnchorElement>("a");

        // No link means VK wrote it without attributing it, which is how it marks your own.
        var sender = link is null ? owner : Contact(link);

        var text = MessageText(element);
        var attachments = Attachments(element);

        return new NormalizedMessage
        {
            Uid = $"vk/{peerId}/{id}",
            SourceThreadId = peerId,
            Kind = "message",
            Sender = sender,
            SentAtUtc = ReadDate(header, page, out var unix),
            SentAtUnix = unix,
            Plaintext = text,
            ContentHash = ContentHash(text, attachments),
            Media = attachments,
        };
    }

    private static NormalizedIdentity Contact(IHtmlAnchorElement link)
    {
        var match = ProfileId.Match(link.Href ?? string.Empty);
        var name = link.TextContent.Trim();

        if (!match.Success)
        {
            // A name with no resolvable profile: keyed by name and flagged, exactly as §2's
            // name-only senders are.
            return new NormalizedIdentity(PlatformId, null, null, name, IsSynthetic: true);
        }

        // Communities are negative ids in VK's own numbering, and keeping that convention means a
        // group and a person can never collide.
        var id = match.Groups["kind"].Value == "id"
            ? match.Groups["id"].Value
            : "-" + match.Groups["id"].Value;

        return new NormalizedIdentity(PlatformId, id, null, string.IsNullOrWhiteSpace(name) ? id : name, false);
    }

    /// <summary>
    /// The date at the end of the header.
    /// </summary>
    /// <remarks>
    /// VK writes local time with no offset, so it is taken as UTC: inventing a timezone would put
    /// every message a few hours from where it belongs, and there is nothing in the archive to
    /// derive the right one from.
    /// </remarks>
    private string ReadDate(IElement header, string page, out long unix)
    {
        // An edit marker sits between the name and the date and would otherwise be read as part
        // of it.
        var text = header.Clone(true) is IElement clone
            ? Strip(clone, "message-edited").TextContent
            : header.TextContent;

        var match = DatePattern.Match(text);

        if (!match.Success)
        {
            throw new InvalidDataException(
                $"Could not read a date from '{text.Trim()}' in '{page}'.");
        }

        var month = match.Groups["month"].Value.TrimEnd('.');

        if (!Months.TryGetValue(month, out var monthNumber))
        {
            throw new InvalidDataException($"'{month}' is not a month this reader knows, in '{page}'.");
        }

        var at = new DateTimeOffset(
            int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
            monthNumber,
            int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture),
            match.Groups["second"].Success
                ? int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture)
                : 0,
            TimeSpan.Zero);

        unix = at.ToUnixTimeSeconds();

        return at.ToString("O");
    }

    private static IElement Strip(IElement element, string className)
    {
        foreach (var found in element.GetElementsByClassName(className).ToArray())
        {
            found.Remove();
        }

        return element;
    }

    /// <summary>
    /// The message's own words, without its attachments or its header.
    /// </summary>
    private static string MessageText(IElement element)
    {
        if (element.Clone(true) is not IElement clone)
        {
            return element.TextContent.Trim();
        }

        Strip(clone, "message__header");
        Strip(clone, "kludges");

        return clone.TextContent.Trim();
    }

    private static List<NormalizedMedia> Attachments(IElement element)
    {
        var media = new List<NormalizedMedia>();

        foreach (var attachment in element.QuerySelectorAll(".kludges .attachment"))
        {
            var description = attachment.QuerySelector(".attachment__description")?.TextContent.Trim();
            var link = attachment.QuerySelector<IHtmlAnchorElement>("a")?.Href;

            // VK's archive describes attachments but does not include the files, so these are
            // recorded as present-but-absent — §2's rule that a missing photo never costs you the
            // message it belonged to.
            media.Add(new NormalizedMedia(
                ExportPath: link ?? description ?? "attachment",
                MediaKind: "file",
                OriginalFilename: null,
                Mime: null,
                MissingReason: "VK archives describe attachments but do not include the files",
                StickerEmoji: null,
                Width: null,
                Height: null,
                DurationSeconds: null));
        }

        return media;
    }

    private static string ContentHash(string plaintext, List<NormalizedMedia> media)
    {
        const char Separator = (char)31;

        var builder = new StringBuilder().Append(plaintext).Append(Separator);

        foreach (var item in media)
        {
            builder.Append(item.ExportPath).Append(Separator);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
