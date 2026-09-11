using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Archive.Import.Sms;

/// <summary>
/// Text messages, from an Android backup made with SMS Backup &amp; Restore.
/// </summary>
/// <remarks>
/// <para>
/// One XML file per backup, <c>sms-&lt;timestamp&gt;.xml</c>, rooted at <c>&lt;smses&gt;</c>:
/// an <c>&lt;sms&gt;</c> element per text, and an <c>&lt;mms&gt;</c> element per picture message
/// with its <c>&lt;parts&gt;</c> — pictures included, as base64 — and its <c>&lt;addrs&gt;</c>.
/// Point at the folder holding the file; several backups in one folder are read together, and a
/// message present in more than one is stored once.
/// </para>
/// <para>
/// Traps:
/// </para>
/// <list type="bullet">
///   <item><b>Emoji are written as two numeric entities</b>, one per UTF-16 surrogate —
///   <c>&amp;#55357;&amp;#56832;</c> — which a conforming XML parser refuses as invalid
///   characters. The reader turns character checking off so the pair reassembles into the emoji it
///   was.</item>
///   <item><b>Missing values are the string <c>null</c></b>, not an absent attribute.</item>
///   <item><b>There are no message ids</b> for texts (MMS carry an <c>m_id</c>, used when present).
///   A text's uid is its number, time, direction and a hash of its body, counted for exact repeats
///   within one file.</item>
///   <item><b>The backup never says whose phone it was.</b> A sent MMS names its own sender, so
///   the owner's number is read from those when there are any; otherwise the preview asks, and
///   without an answer "me" is a placeholder.</item>
///   <item><b>Drafts are not messages.</b> Nobody received them, and they are skipped by type
///   rather than imported as though they were sent.</item>
/// </list>
/// <para>
/// People are keyed by phone number — the strongest identity key any format here has, and one
/// Telegram, WhatsApp and Signal share. Numbers are normalized only by removing formatting; a
/// number stored with and without its country code stays two numbers, because adding one would be
/// a guess about which country the phone was in.
/// </para>
/// </remarks>
public sealed class SmsBackupImporter : IPlatformImporter
{
    public const string PlatformId = "sms";

    /// <summary>PduHeaders: who an MMS address is.</summary>
    private const int From = 137;

    private static readonly XmlReaderSettings Settings = new()
    {
        // Emoji arrive as surrogate-pair character references, which strict checking rejects.
        CheckCharacters = false,
        DtdProcessing = DtdProcessing.Ignore,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    public string Platform => PlatformId;

    public string DisplayName => "SMS and MMS (SMS Backup & Restore)";

    public ImportDetection Detect(string path)
    {
        var files = BackupFiles(path);

        if (files.Length == 0)
        {
            return ImportDetection.No;
        }

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: null,
            AccountName: null,
            FileCount: files.Length,
            Note: "A backup does not say whose phone it came from. Your number is read from picture "
                + "messages you sent, when there are any — tell the app your number and your texts "
                + "are attributed to you either way. Drafts are not imported.",
            AccountIdIsGuess: true);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var files = BackupFiles(path);

        if (files.Length == 0)
        {
            throw new InvalidDataException(
                $"'{path}' holds no SMS Backup & Restore file. Point at the folder with the sms-….xml backup in it.");
        }

        var owner = Owner(path, files, options?.OwnerAccountId);
        sink.OnOwner(owner);

        var announced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            ReadFile(file, owner, sink, announced);
        }
    }

    /// <summary>Backup files in the folder: XML whose root is <c>smses</c>. Call logs are <c>calls</c> and are not.</summary>
    private static string[] BackupFiles(string path)
    {
        if (File.Exists(path))
        {
            return IsBackup(path) ? [path] : [];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(path, "*.xml", SearchOption.TopDirectoryOnly)
            .Where(IsBackup)
            .Order(StringComparer.Ordinal)];
    }

    private static bool IsBackup(string file)
    {
        using var stream = File.OpenRead(file);
        var buffer = new byte[2048];
        var read = stream.Read(buffer);

        return Encoding.UTF8.GetString(buffer, 0, read).Contains("<smses", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whose phone this was: what the user said, the sender of their own picture messages, or a
    /// placeholder flagged as the guess it is.
    /// </summary>
    private static NormalizedIdentity Owner(string path, string[] files, string? stated)
    {
        if (!string.IsNullOrWhiteSpace(stated))
        {
            return new NormalizedIdentity(PlatformId, Normalize(stated), stated.Trim(), "You", IsSynthetic: false);
        }

        if (OwnNumber(files) is { } number)
        {
            return new NormalizedIdentity(PlatformId, number, number, "You", IsSynthetic: false);
        }

        var placeholder = "folder:" + new DirectoryInfo(
            Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path).Name;

        return new NormalizedIdentity(
            PlatformId, placeholder, Handle: null, "You (number not identified)", IsSynthetic: true);
    }

    /// <summary>
    /// The number the owner's sent MMS name as their sender, by majority of the first fifty.
    /// </summary>
    /// <remarks>
    /// A phone writes its own number as the From address on what it sends, which is as close as a
    /// backup comes to saying whose it is. Fifty is enough to outvote a dual-SIM oddity without
    /// reading a multi-gigabyte file twice.
    /// </remarks>
    private static string? OwnNumber(string[] files)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = 0;

        foreach (var file in files)
        {
            using var reader = XmlReader.Create(file, Settings);

            while (seen < 50 && reader.ReadToFollowing("mms"))
            {
                if (reader.GetAttribute("msg_box") != "2")
                {
                    continue;
                }

                if (XNode.ReadFrom(reader) is not XElement mms)
                {
                    continue;
                }

                seen++;

                var from = Addresses(mms).FirstOrDefault(a => a.Type == From).Address;

                if (from is not null)
                {
                    counts[from] = counts.GetValueOrDefault(from) + 1;
                }
            }
        }

        return counts.Count == 0 ? null : counts.MaxBy(p => p.Value).Key;
    }

    private void ReadFile(string file, NormalizedIdentity owner, IImportSink sink, HashSet<string> announced)
    {
        using var reader = XmlReader.Create(file, Settings);

        reader.MoveToContent();

        if (reader.LocalName != "smses")
        {
            throw new InvalidDataException($"'{file}' is not rooted at <smses>.");
        }

        // Counted per file, so a message present in two overlapping backups gets the same uid in
        // both and is stored once, while two identical texts in one backup stay two.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        reader.Read();

        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            var name = reader.LocalName;

            if (XNode.ReadFrom(reader) is not XElement element)
            {
                continue;
            }

            var read = name switch
            {
                "sms" => ReadSms(file, element, owner, seen),
                "mms" => ReadMms(file, element, owner, seen),
                _ => throw new InvalidDataException(
                    $"'{file}' has a <{name}> element, which this reader does not know."),
            };

            if (read is not { } found)
            {
                continue;
            }

            if (announced.Add(found.Thread.SourceThreadId))
            {
                sink.OnThread(found.Thread);
            }

            sink.OnMessage(found.Thread, found.Message);
        }
    }

    private static (NormalizedThread Thread, NormalizedMessage Message)? ReadSms(
        string file, XElement sms, NormalizedIdentity owner, Dictionary<string, int> seen)
    {
        var type = Int(sms, "type", file);

        // 1 inbox; 2 sent, 4 outbox, 5 failed, 6 queued — all written by the owner; 3 is a draft.
        var outgoing = type switch
        {
            1 => false,
            2 or 4 or 5 or 6 => true,
            3 => (bool?)null,
            _ => throw new InvalidDataException($"An <sms> in '{file}' has type {type}, which this reader does not know."),
        };

        if (outgoing is not { } isOutgoing)
        {
            return null;
        }

        var address = Normalize(Attr(sms, "address")
            ?? throw new InvalidDataException($"An <sms> in '{file}' has no address."));

        var at = DateTimeOffset.FromUnixTimeMilliseconds(Millis(sms, file));
        var body = Attr(sms, "body") ?? string.Empty;
        var other = Person(address, ContactName(Attr(sms, "contact_name")));

        var thread = new NormalizedThread($"dm/{address}", "dm", other.DisplayName, [other]);

        var key = $"{at.ToUnixTimeMilliseconds()}/{(isOutgoing ? "out" : "in")}/{Hash(body)[..12]}";
        var occurrence = seen.GetValueOrDefault(key);
        seen[key] = occurrence + 1;

        return (thread, new NormalizedMessage
        {
            Uid = $"sms/{address}/{key}/{occurrence}",
            SourceThreadId = thread.SourceThreadId,
            Kind = "message",
            Sender = isOutgoing ? owner : other,
            SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
            SentAtUnix = at.ToUnixTimeSeconds(),
            Plaintext = body,
            ContentHash = Hash(body),
            RawJson = sms.ToString(SaveOptions.DisableFormatting),
        });
    }

    private static (NormalizedThread Thread, NormalizedMessage Message)? ReadMms(
        string file, XElement mms, NormalizedIdentity owner, Dictionary<string, int> seen)
    {
        var box = Int(mms, "msg_box", file);

        var outgoing = box switch
        {
            1 => false,
            2 or 4 => true,
            3 => (bool?)null,
            _ => throw new InvalidDataException($"An <mms> in '{file}' has msg_box {box}, which this reader does not know."),
        };

        if (outgoing is not { } isOutgoing)
        {
            return null;
        }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(Millis(mms, file));
        var addresses = Addresses(mms);
        var own = owner.IsSynthetic ? null : owner.SourceIdentityId;

        var from = addresses.FirstOrDefault(a => a.Type == From).Address;

        // Everyone but the owner. When the owner's number is unknown, the From on a sent message
        // is still theirs — it is the only address a sent message can be from.
        var others = addresses
            .Where(a => a.Address != own && !(isOutgoing && a.Type == From))
            .Select(a => a.Address)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (others.Length == 0)
        {
            throw new InvalidDataException($"An <mms> in '{file}' names nobody but its sender.");
        }

        var names = Names(mms, others);
        var people = others.Select(a => Person(a, names.GetValueOrDefault(a))).ToArray();

        var sender = isOutgoing || from is null || from == own
            ? owner
            : people.FirstOrDefault(p => p.SourceIdentityId == from) ?? Person(from, names.GetValueOrDefault(from));

        var thread = people.Length == 1
            ? new NormalizedThread($"dm/{others[0]}", "dm", people[0].DisplayName, people)
            : new NormalizedThread(
                "group/" + string.Join(",", others.Order(StringComparer.Ordinal)),
                "group",
                string.Join(", ", people.Select(p => p.DisplayName)),
                people);

        var (text, media) = Parts(mms, file);

        var identity = new StringBuilder(text);

        foreach (var item in media)
        {
            identity.Append(Separator).Append(item.Mime).Append(Separator)
                .Append(item.Content is { } bytes ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : "missing");
        }

        var hash = Hash(identity.ToString());

        string uid;

        if (Attr(mms, "m_id") is { } mId)
        {
            uid = $"mms/{mId}";
        }
        else
        {
            var key = $"{at.ToUnixTimeMilliseconds()}/{(isOutgoing ? "out" : "in")}/{hash[..12]}";
            var occurrence = seen.GetValueOrDefault(key);
            seen[key] = occurrence + 1;
            uid = $"mms/{thread.SourceThreadId}/{key}/{occurrence}";
        }

        return (thread, new NormalizedMessage
        {
            Uid = uid,
            SourceThreadId = thread.SourceThreadId,
            Kind = "message",
            Sender = sender,
            SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
            SentAtUnix = at.ToUnixTimeSeconds(),
            Plaintext = text,
            ContentHash = hash,
            RawJson = WithoutData(mms),
            Media = media,
        });
    }

    private const char Separator = (char)31;

    /// <summary>Addresses from <c>&lt;addrs&gt;</c>, or from the <c>~</c>-joined attribute when there are none.</summary>
    private static List<(string Address, int Type)> Addresses(XElement mms)
    {
        var list = new List<(string, int)>();

        foreach (var addr in mms.Elements("addrs").Elements("addr"))
        {
            if (Attr(addr, "address") is { } address &&
                int.TryParse(Attr(addr, "type"), NumberStyles.None, CultureInfo.InvariantCulture, out var type))
            {
                list.Add((Normalize(address), type));
            }
        }

        if (list.Count == 0 && Attr(mms, "address") is { } joined)
        {
            list.AddRange(joined.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(a => (Normalize(a), 151)));
        }

        return list;
    }

    /// <summary>
    /// Names for the addresses, from <c>contact_name</c>.
    /// </summary>
    /// <remarks>
    /// A group MMS writes its names comma-joined in the order of the <c>~</c>-joined address. They
    /// are paired up only when the counts agree; a name attached to the wrong number would be worse
    /// than showing the number.
    /// </remarks>
    private static Dictionary<string, string> Names(XElement mms, string[] others)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var contact = ContactName(Attr(mms, "contact_name"));
        var joined = Attr(mms, "address");

        if (contact is null || joined is null)
        {
            return names;
        }

        var addresses = joined.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
        var parts = contact.Split(", ");

        if (addresses.Length == parts.Length)
        {
            for (var i = 0; i < addresses.Length; i++)
            {
                names[addresses[i]] = parts[i];
            }
        }
        else if (others.Length == 1)
        {
            names[others[0]] = contact;
        }

        return names;
    }

    private static (string Text, List<NormalizedMedia> Media) Parts(XElement mms, string file)
    {
        var text = new List<string>();
        var media = new List<NormalizedMedia>();

        if (Attr(mms, "sub") is { } subject)
        {
            text.Add(subject);
        }

        foreach (var part in mms.Elements("parts").Elements("part"))
        {
            var type = (Attr(part, "ct") ?? "application/octet-stream").ToLowerInvariant();

            if (type == "application/smil")
            {
                continue;
            }

            if (type == "text/plain")
            {
                if (Attr(part, "text") is { } words)
                {
                    text.Add(words);
                }

                continue;
            }

            var name = Attr(part, "name") ?? Attr(part, "cl") ?? Attr(part, "fn") ?? "attachment";

            if (Path.GetExtension(name).Length == 0 && Extension(type) is { } extension)
            {
                name += extension;
            }

            byte[]? content = null;

            if (Attr(part, "data") is { } data)
            {
                try
                {
                    content = Convert.FromBase64String(data);
                }
                catch (FormatException)
                {
                    throw new InvalidDataException($"An MMS part in '{file}' has data that is not base64.");
                }
            }

            media.Add(new NormalizedMedia(
                $"mms/{name}", KindOf(type), name, type,
                MissingReason: content is null ? "the backup was made without MMS attachments" : null,
                StickerEmoji: null, Width: null, Height: null, DurationSeconds: null)
            {
                Content = content,
            });
        }

        return (string.Join("\n", text), media);
    }

    private static string KindOf(string mime) => mime switch
    {
        "image/gif" => "animation",
        _ when mime.StartsWith("image/", StringComparison.Ordinal) => "photo",
        _ when mime.StartsWith("video/", StringComparison.Ordinal) => "video",
        "audio/amr" or "audio/3gpp" or "audio/ogg" => "voice",
        _ when mime.StartsWith("audio/", StringComparison.Ordinal) => "audio",
        _ => "file",
    };

    private static string? Extension(string mime) => mime switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "video/3gpp" => ".3gp",
        "audio/amr" => ".amr",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "text/x-vcard" or "text/vcard" => ".vcf",
        _ => null,
    };

    /// <summary>The element as it came, minus the base64 — the pictures are stored once already.</summary>
    private static string WithoutData(XElement mms)
    {
        var copy = new XElement(mms);

        foreach (var part in copy.Elements("parts").Elements("part"))
        {
            part.Attribute("data")?.Remove();
        }

        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static NormalizedIdentity Person(string address, string? name) =>
        new(PlatformId, address, address, name ?? address, IsSynthetic: false);

    /// <summary>
    /// A phone number with its formatting removed, and nothing else changed.
    /// </summary>
    /// <remarks>
    /// Spaces, dashes, dots and brackets go; a leading + stays. An alphanumeric sender — a bank, a
    /// delivery service — is kept as written. No country code is added: which country a local
    /// number belonged to is not something the backup says.
    /// </remarks>
    internal static string Normalize(string address)
    {
        var trimmed = address.Trim();

        if (trimmed.Length == 0)
        {
            throw new InvalidDataException("An SMS address is empty.");
        }

        if (trimmed.Any(char.IsLetter))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);

        foreach (var c in trimmed)
        {
            if (char.IsAsciiDigit(c) || (c == '+' && builder.Length == 0))
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? trimmed : builder.ToString();
    }

    private static string? ContactName(string? name) =>
        name is null or "(Unknown)" || string.IsNullOrWhiteSpace(name) ? null : name;

    /// <summary>An attribute, with the backup's literal <c>null</c> read as absent.</summary>
    private static string? Attr(XElement element, string name) =>
        element.Attribute(name)?.Value is { } value && value != "null" ? value : null;

    private static int Int(XElement element, string name, string file) =>
        int.TryParse(Attr(element, name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"An <{element.Name.LocalName}> in '{file}' has no {name}.");

    /// <summary>
    /// The date, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Anything before 1973 is refused rather than reinterpreted: it would mean a value in seconds
    /// from some other tool, and multiplying by a thousand on a hunch is how an archive ends up
    /// quietly wrong.
    /// </remarks>
    private static long Millis(XElement element, string file)
    {
        if (!long.TryParse(Attr(element, "date"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
        {
            throw new InvalidDataException($"An <{element.Name.LocalName}> in '{file}' has no date.");
        }

        if (ms < 100_000_000_000L)
        {
            throw new InvalidDataException(
                $"An <{element.Name.LocalName}> in '{file}' has date {ms}, which is not milliseconds since 1970.");
        }

        return ms;
    }

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
}
