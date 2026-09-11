using System.Globalization;
using System.Text;

namespace Archive.Import.Synthetic;

/// <summary>Which phone exported the chat.</summary>
public enum WhatsAppStyle
{
    /// <summary><c>[14/03/2021, 22:41:03] Sam: text</c>, in <c>WhatsApp Chat - Sam/_chat.txt</c>.</summary>
    Ios,

    /// <summary><c>14/03/2021, 22:41 - Sam: text</c>, in <c>WhatsApp Chat with Sam.txt</c>.</summary>
    Android,
}

/// <summary>
/// Builds a WhatsApp "Export chat" text file, as a real file on disk.
/// </summary>
/// <remarks>
/// Times are wall clock, as WhatsApp writes them, so they are given as <see cref="DateTime"/>.
/// The iPhone's invisible marks — a left-to-right mark before notices and attachments, a narrow
/// no-break space before AM and PM — are written too, because a reader that has only ever seen
/// clean text will fail on the first real export.
/// </remarks>
public sealed class WhatsAppChatBuilder
{
    private static readonly string Lrm = ((char)0x200E).ToString();
    private static readonly string NarrowSpace = ((char)0x202F).ToString();

    private readonly string _name;
    private readonly WhatsAppStyle _style;
    private readonly string _dateFormat;
    private readonly bool _twelveHour;
    private readonly List<string> _lines = [];
    private readonly List<(string File, int Seed)> _files = [];

    private WhatsAppChatBuilder(string name, WhatsAppStyle style, string dateFormat, bool twelveHour)
    {
        _name = name;
        _style = style;
        _dateFormat = dateFormat;
        _twelveHour = twelveHour;
    }

    /// <param name="dateFormat">The phone's date format: <c>dd/MM/yyyy</c>, <c>M/d/yy</c>, <c>dd.MM.yy</c>.</param>
    public static WhatsAppChatBuilder New(
        string chatName, WhatsAppStyle style = WhatsAppStyle.Ios, string dateFormat = "dd/MM/yyyy", bool twelveHour = false) =>
        new(chatName, style, dateFormat, twelveHour);

    /// <summary>A message; newlines in <paramref name="text"/> become continuation lines.</summary>
    public WhatsAppChatBuilder Message(DateTime at, string sender, string text) =>
        Raw($"{Header(at)}{sender}: {text}");

    /// <param name="seed">Writes the file beside the chat when given; null leaves it out.</param>
    public WhatsAppChatBuilder Attachment(DateTime at, string sender, string file, int? seed, string? caption = null)
    {
        if (seed is { } s)
        {
            _files.Add((file, s));
        }

        return _style == WhatsAppStyle.Ios
            ? Raw($"{Header(at)}{sender}: {Lrm}<attached: {file}>" + (caption is null ? string.Empty : "\n" + caption))
            : Raw($"{Header(at)}{sender}: {file} (file attached)" + (caption is null ? string.Empty : "\n" + caption));
    }

    /// <summary>An attachment left out of an export made without media.</summary>
    public WhatsAppChatBuilder Omitted(DateTime at, string sender, string what = "image") =>
        _style == WhatsAppStyle.Ios
            ? Raw($"{Header(at)}{sender}: {Lrm}{what} omitted")
            : Raw($"{Header(at)}{sender}: <Media omitted>");

    /// <summary>A notice WhatsApp itself wrote: encryption, someone joining, a changed subject.</summary>
    public WhatsAppChatBuilder Notice(DateTime at, string text) =>
        _style == WhatsAppStyle.Ios
            ? Raw($"{Header(at)}{_name}: {Lrm}{text}")
            : Raw($"{Header(at)}{text}");

    public WhatsAppChatBuilder Raw(string line)
    {
        _lines.Add(line);

        return this;
    }

    private string Header(DateTime at)
    {
        var culture = CultureInfo.InvariantCulture;
        var date = at.ToString(_dateFormat, culture);

        var time = _style == WhatsAppStyle.Ios
            ? _twelveHour ? at.ToString("h:mm:ss", culture) + NarrowSpace + at.ToString("tt", culture) : at.ToString("HH:mm:ss", culture)
            : _twelveHour ? at.ToString("h:mm", culture) + NarrowSpace + at.ToString("tt", culture) : at.ToString("HH:mm", culture);

        return _style == WhatsAppStyle.Ios ? $"[{date}, {time}] " : $"{date}, {time} - ";
    }

    /// <summary>Where the chat file lands inside <paramref name="folder"/>.</summary>
    public string ChatFile(string folder) =>
        _style == WhatsAppStyle.Ios
            ? Path.Combine(folder, $"WhatsApp Chat - {_name}", "_chat.txt")
            : Path.Combine(folder, $"WhatsApp Chat with {_name}.txt");

    /// <summary>Writes the chat and its attachments into <paramref name="folder"/> and returns the folder.</summary>
    public string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var file = ChatFile(folder);
        var directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);

        var newline = _style == WhatsAppStyle.Ios ? "\r\n" : "\n";
        File.WriteAllText(file, string.Join(newline, _lines.Select(l => l.Replace("\n", newline, StringComparison.Ordinal))) + newline, new UTF8Encoding(false));

        foreach (var (name, seed) in _files)
        {
            SyntheticMedia.Write(directory, name, seed);
        }

        return folder;
    }
}
