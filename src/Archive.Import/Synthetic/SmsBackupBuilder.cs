using System.Globalization;
using System.Text;

namespace Archive.Import.Synthetic;

/// <summary>One MMS part that is not text: a content type, a name and, unless left out, its bytes.</summary>
public sealed record SmsMmsPart(string ContentType, string Name, byte[]? Data);

/// <summary>
/// Builds an SMS Backup &amp; Restore XML file, as a real file on disk.
/// </summary>
/// <remarks>
/// Written by hand rather than through an XML writer, because the thing under test is the app's
/// own quirks: every missing value is the string <c>null</c>, and characters outside the Basic
/// Multilingual Plane are written as two surrogate character references, which a conforming writer
/// would refuse to produce.
/// </remarks>
public sealed class SmsBackupBuilder
{
    private readonly List<string> _elements = [];

    public static SmsBackupBuilder New() => new();

    /// <param name="type">1 received, 2 sent, 3 draft, 4 outbox, 5 failed, 6 queued.</param>
    public SmsBackupBuilder Sms(string address, DateTimeOffset at, int type, string body, string? contactName = null)
    {
        _elements.Add(
            $"<sms protocol=\"0\" address=\"{Escape(address)}\" date=\"{Ms(at)}\" type=\"{type}\" subject=\"null\" "
            + $"body=\"{Escape(body)}\" toa=\"null\" sc_toa=\"null\" service_center=\"null\" read=\"1\" status=\"-1\" "
            + $"locked=\"0\" date_sent=\"0\" sub_id=\"1\" readable_date=\"whatever\" contact_name=\"{Escape(contactName ?? "(Unknown)")}\" />");

        return this;
    }

    /// <param name="box">1 inbox, 2 sent, 3 draft, 4 outbox.</param>
    /// <param name="addresses">Each with its PduHeaders type: 137 from, 151 to, 130 cc.</param>
    public SmsBackupBuilder Mms(
        DateTimeOffset at,
        int box,
        IReadOnlyList<(string Address, int Type)> addresses,
        string? text,
        SmsMmsPart? part = null,
        string? mId = null,
        string? contactName = null)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var others = addresses.Where(a => a.Type != 137 || box == 1).Select(a => a.Address);

        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"<mms date=\"{Ms(at)}\" rr=\"null\" sub=\"null\" ct_t=\"application/vnd.wap.multipart.related\" ")
            .Append(CultureInfo.InvariantCulture,
                $"msg_box=\"{box}\" address=\"{Escape(string.Join("~", others))}\" m_id=\"{Escape(mId ?? "null")}\" ")
            .Append(CultureInfo.InvariantCulture,
                $"read=\"1\" text_only=\"{(part is null ? 1 : 0)}\" contact_name=\"{Escape(contactName ?? "(Unknown)")}\">")
            .Append("<parts>")
            .Append("<part seq=\"-1\" ct=\"application/smil\" name=\"null\" chset=\"null\" cl=\"smil.xml\" text=\"&lt;smil&gt;&lt;/smil&gt;\" />");

        if (text is not null)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"<part seq=\"0\" ct=\"text/plain\" name=\"null\" chset=\"106\" cl=\"text_0.txt\" text=\"{Escape(text)}\" />");
        }

        if (part is not null)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"<part seq=\"0\" ct=\"{part.ContentType}\" name=\"{Escape(part.Name)}\" chset=\"null\" cl=\"{Escape(part.Name)}\" text=\"null\"");

            if (part.Data is not null)
            {
                builder.Append(CultureInfo.InvariantCulture, $" data=\"{Convert.ToBase64String(part.Data)}\"");
            }

            builder.Append(" />");
        }

        builder.Append("</parts><addrs>");

        foreach (var (address, type) in addresses)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<addr address=\"{Escape(address)}\" type=\"{type}\" charset=\"106\" />");
        }

        builder.Append("</addrs></mms>");
        _elements.Add(builder.ToString());

        return this;
    }

    /// <summary>An element of a kind the format does not have, for testing the refusal.</summary>
    public SmsBackupBuilder Raw(string element)
    {
        _elements.Add(element);

        return this;
    }

    public string Xml() =>
        "<?xml version='1.0' encoding='UTF-8' standalone='yes' ?>\n"
        + "<!--File Created By SMS Backup & Restore v10.19.001 on 14/03/2021 22:41:03-->\n"
        + $"<smses count=\"{_elements.Count}\" backup_set=\"00000000-0000-0000-0000-000000000000\" backup_date=\"1615757463000\" type=\"full\">\n"
        + string.Join("\n", _elements)
        + "\n</smses>\n";

    /// <summary>Writes the backup into <paramref name="folder"/> and returns the folder.</summary>
    public string Write(string folder, string fileName = "sms-20210314224103.xml")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), Xml(), new UTF8Encoding(false));

        return folder;
    }

    private static string Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// XML escaping as the app does it: emoji as two surrogate references, everything else literal.
    /// </summary>
    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\n': builder.Append("&#10;"); break;
                case var s when char.IsSurrogate(s):
                    builder.Append(CultureInfo.InvariantCulture, $"&#{(int)s};");
                    break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }
}
