using System.Globalization;
using System.Net;
using System.Text;

namespace Archive.Import.Synthetic;

/// <summary>A phone contact: a number, and the name Google Voice shows for it.</summary>
public sealed record VoiceContact(string Number, string Name);

/// <summary>
/// Builds Takeout's <c>Voice/Calls</c> folder, as real files on disk.
/// </summary>
/// <remarks>
/// The markup is written to Google's hCard and hAtom shape — <c>div.message</c>, <c>abbr.dt</c>
/// with an ISO title, <c>cite.vcard</c> around a <c>tel:</c> link, "Me" for the owner — because
/// those classes are all a reader has to hold on to.
/// </remarks>
public sealed class GoogleVoiceExportBuilder
{
    private readonly string _owner;
    private readonly List<(string Name, string Html)> _files = [];
    private readonly List<(string Name, int Seed)> _media = [];

    private GoogleVoiceExportBuilder(string owner) => _owner = owner;

    public static GoogleVoiceExportBuilder New(string ownerNumber) => new(ownerNumber);

    /// <summary>One file of texts with one person; <paramref name="started"/> names the file.</summary>
    public GoogleVoiceExportBuilder Text(VoiceContact other, DateTimeOffset started, Action<GoogleVoiceTextBuilder> messages)
    {
        var name = $"{other.Name} - Text - {Stamp(started)}";
        var body = new StringBuilder();
        messages(new GoogleVoiceTextBuilder(body, _owner, _media, name));

        _files.Add((name, Page(other.Name, $"<div class=\"hChatLog hfeed\">{body}</div>")));

        return this;
    }

    public GoogleVoiceExportBuilder Group(
        IReadOnlyList<VoiceContact> participants, DateTimeOffset started, Action<GoogleVoiceTextBuilder> messages)
    {
        ArgumentNullException.ThrowIfNull(participants);

        var name = $"Group Conversation - {Stamp(started)}";
        var body = new StringBuilder();
        messages(new GoogleVoiceTextBuilder(body, _owner, _media, name));

        var roster = string.Join(", ", participants.Select(p => Cite(p.Number, p.Name, "participant")));

        _files.Add((name, Page("Group Conversation",
            $"<div class=\"participants\">Group conversation with:\n{roster}</div><div class=\"hChatLog hfeed\">{body}</div>")));

        return this;
    }

    /// <param name="kind"><c>Received</c>, <c>Placed</c>, <c>Missed</c> or <c>Voicemail</c> — or anything else, to test the refusal.</param>
    /// <param name="audioSeed">For a voicemail, writes its audio beside the file.</param>
    public GoogleVoiceExportBuilder Call(
        VoiceContact other, string kind, DateTimeOffset at, TimeSpan duration, string? transcript = null, int? audioSeed = null)
    {
        ArgumentNullException.ThrowIfNull(other);

        var name = $"{other.Name} - {kind} - {Stamp(at)}";
        var verb = kind switch
        {
            "Placed" => "Placed call to",
            "Missed" => "Missed call from",
            "Voicemail" => "Voicemail from",
            _ => "Received call from",
        };

        var html = new StringBuilder()
            .Append("<div class=\"haudio\">")
            .Append(CultureInfo.InvariantCulture, $"<span class=\"fn\">{verb}</span>\n")
            .Append(CultureInfo.InvariantCulture, $"<a class=\"tel\" href=\"tel:{other.Number}\"><span class=\"fn\">{WebUtility.HtmlEncode(other.Name)}</span></a>\n")
            .Append(CultureInfo.InvariantCulture, $"<abbr class=\"published\" title=\"{Iso(at)}\">{at.ToString("MMM d, yyyy, h:mm:ss tt", CultureInfo.InvariantCulture)}</abbr>\n")
            .Append(CultureInfo.InvariantCulture, $"<abbr class=\"duration\" title=\"{System.Xml.XmlConvert.ToString(duration)}\">({duration:hh\\:mm\\:ss})</abbr>");

        if (transcript is not null)
        {
            html.Append(CultureInfo.InvariantCulture, $"<span class=\"description\"><span class=\"full-text\">{WebUtility.HtmlEncode(transcript)}</span></span>");
        }

        if (audioSeed is { } seed)
        {
            var audio = name + ".mp3";
            _media.Add((audio, seed));
            html.Append(CultureInfo.InvariantCulture, $"<audio controls=\"controls\" src=\"{Uri.EscapeDataString(audio)}\"></audio>");
        }

        html.Append("</div>");
        _files.Add((name, Page(other.Name, html.ToString())));

        return this;
    }

    /// <summary>Writes <c>Takeout/Voice/Calls</c> inside <paramref name="root"/> and returns the root.</summary>
    public string Write(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var calls = Path.Combine(root, "Takeout", "Voice", "Calls");
        Directory.CreateDirectory(calls);

        foreach (var (name, html) in _files)
        {
            File.WriteAllText(Path.Combine(calls, name + ".html"), html, new UTF8Encoding(false));
        }

        foreach (var (name, seed) in _media)
        {
            SyntheticMedia.Write(calls, name, seed);
        }

        return root;
    }

    internal static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH'_'mm'_'ss'Z'", CultureInfo.InvariantCulture);

    internal static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    internal static string Cite(string number, string name, string @class) =>
        $"<cite class=\"{@class} vcard\"><a class=\"tel\" href=\"tel:{number}\"><span class=\"fn\">{WebUtility.HtmlEncode(name)}</span></a></cite>";

    private static string Page(string title, string body) =>
        "<?xml version=\"1.0\" ?>\n<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">\n"
        + $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta charset=\"UTF-8\" /><title>{WebUtility.HtmlEncode(title)}</title></head>"
        + $"<body>{body}</body></html>";
}

/// <summary>The messages of one text file.</summary>
public sealed class GoogleVoiceTextBuilder(StringBuilder body, string owner, List<(string Name, int Seed)> media, string fileName)
{
    private int _pictures;

    /// <param name="from">Null for the owner, who Google Voice signs as "Me".</param>
    /// <param name="pictureSeed">Attaches a picture, written beside the file.</param>
    public GoogleVoiceTextBuilder Message(DateTimeOffset at, VoiceContact? from, string text, int? pictureSeed = null)
    {
        var sender = from is null
            ? $"<cite class=\"sender vcard\"><a class=\"tel\" href=\"tel:{owner}\"><abbr class=\"fn\" title=\"\">Me</abbr></a></cite>"
            : GoogleVoiceExportBuilder.Cite(from.Number, from.Name, "sender");

        body.Append("<div class=\"message\">")
            .Append(CultureInfo.InvariantCulture, $"<abbr class=\"dt\" title=\"{GoogleVoiceExportBuilder.Iso(at)}\">{at.ToString("MMM d, yyyy, h:mm:ss tt", CultureInfo.InvariantCulture)}</abbr>:\n")
            .Append(sender).Append(":\n")
            .Append("<q>").Append(WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</q>");

        if (pictureSeed is { } seed)
        {
            var picture = $"{fileName}-{++_pictures}-1.jpg";
            media.Add((picture, seed));
            body.Append(CultureInfo.InvariantCulture, $"<img src=\"{Uri.EscapeDataString(picture)}\" alt=\"Image MMS Attachment\" />");
        }

        body.Append("</div>\n");

        return this;
    }
}
