using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Archive.Import.Sms;

namespace Archive.Import.WhatsApp;

/// <summary>
/// WhatsApp's "Export chat", from iPhone or Android.
/// </summary>
/// <remarks>
/// <para>
/// One text file per chat — <c>_chat.txt</c> inside <c>WhatsApp Chat - &lt;name&gt;</c> from an
/// iPhone, <c>WhatsApp Chat with &lt;name&gt;.txt</c> from Android — with the attachments beside it
/// when the chat was exported with media. Point at the folder holding one or several of them.
/// </para>
/// <para>
/// This is the format where strictness genuinely fights the data, and every concession is named:
/// </para>
/// <list type="bullet">
///   <item><b>Dates are in the phone's locale, and <c>03/04/21</c> is two different days.</b> The
///   order is decided once for the whole folder: a day above twelve settles it, and failing that,
///   the reading that keeps each chat in order wins — the wrong one jumps back months every time
///   the month changes. When neither tells them apart the import stops and says so; it never
///   defaults. Dotted dates are read day first, the only order any locale writes them in, and a
///   two-digit year is this century, since WhatsApp began in 2009.</item>
///   <item><b>Times have no zone.</b> They are stored as written and taken as UTC — the same choice
///   the VK reader makes, and for the same reason: there is nothing in the file to derive the
///   right offset from, and inventing one would move every message.</item>
///   <item><b>There are no ids.</b> A message's uid is its chat, minute, sender and a hash of its
///   text, with an occurrence count for exact repeats. An edit is therefore a new message, and a
///   chat whose messages were deleted between exports re-imports what is left without harm.</item>
///   <item><b>Senders are names from the phone's contacts</b>, or the number when there was no
///   contact. A number is keyed by the number — formatted the way the SMS reader formats it, so the
///   same person lines up across the two — and a name is keyed by name and flagged.</item>
///   <item><b>A message runs until the next line that starts like a message</b>, so a line that
///   happens to begin with a date inside someone's text splits it. No reading of the format can
///   avoid that; it is noted here so it is not rediscovered as a defect.</item>
///   <item><b>The export never names its owner.</b> The person present in every chat is, when
///   there are two chats or more to compare; otherwise the preview asks.</item>
/// </list>
/// </remarks>
public sealed partial class WhatsAppImporter : IPlatformImporter
{
    public const string PlatformId = "whatsapp";

    private const char Lrm = (char)0x200E;
    private const char Bom = (char)0xFEFF;
    private const char Separator = (char)31;

    [GeneratedRegex(@"^\[(?<date>\d{1,4}[./-]\d{1,2}[./-]\d{1,4}),?\s+(?<time>\d{1,2}[:.]\d{2}(?:[:.]\d{2})?(?:\s*[AaPp]\.?\s?[Mm]\.?)?)\]\s(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex IosHeader();

    [GeneratedRegex(@"^(?<date>\d{1,4}[./-]\d{1,2}[./-]\d{1,4}),?\s+(?<time>\d{1,2}[:.]\d{2}(?:[:.]\d{2})?(?:\s*[AaPp]\.?\s?[Mm]\.?)?)\s[-–]\s(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidHeader();

    [GeneratedRegex(@"^<attached:\s*(?<file>[^>]+)>$", RegexOptions.CultureInvariant)]
    private static partial Regex IosAttachment();

    [GeneratedRegex(@"^(?<file>\S.*?)\s\(file attached\)$", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidAttachment();

    [GeneratedRegex(@"^(?:(?<kind>image|video|audio|sticker|GIF|document|Contact card) omitted|<Media omitted>)$", RegexOptions.CultureInvariant)]
    private static partial Regex Omitted();

    [GeneratedRegex(@"^\+?[\d\s\-().]{7,}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumber();

    public string Platform => PlatformId;

    public string DisplayName => "WhatsApp";

    public ImportDetection Detect(string path)
    {
        var files = ChatFiles(path);

        if (files.Length == 0)
        {
            return ImportDetection.No;
        }

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: null,
            AccountName: null,
            FileCount: files.Length,
            Note: "WhatsApp exports have no ids and no time zone: times are kept as written and read as "
                + "UTC, and a message edited between two exports arrives as a second one. The export "
                + "does not say which sender is you — tell the app your name as it appears in the chat.",
            AccountIdIsGuess: true,
            AccountCandidates: Candidates(files));
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var files = ChatFiles(path);

        if (files.Length == 0)
        {
            throw new InvalidDataException(
                $"'{path}' holds no WhatsApp chat export. Point at the folder with the exported .txt in it.");
        }

        var chats = files.Select(Parse).ToArray();
        var orders = DateOrders(chats);

        var owner = OwnerName(chats, options?.OwnerAccountId) is { } name ? Person(name) : null;

        if (owner is not null)
        {
            sink.OnOwner(owner);
        }

        foreach (var chat in chats)
        {
            ReadChat(path, chat, orders[chat], owner, sink);
        }
    }

    /// <summary>
    /// The date order of each chat.
    /// </summary>
    /// <remarks>
    /// Per chat, because one folder can hold exports from two phones — an iPhone set to day-first
    /// beside an Android set to month-first. A chat too short to say lends nothing and borrows
    /// the order of the chats written in the same shape (the same separator and the same length of
    /// year), which in practice means the same phone; when those disagree or there are none, it is
    /// refused.
    /// </remarks>
    private static Dictionary<Chat, Order> DateOrders(IReadOnlyList<Chat> chats)
    {
        var decided = new Dictionary<Chat, Order>();
        var undecided = new List<(Chat Chat, string Reason)>();

        foreach (var chat in chats)
        {
            if (TryDateOrder([.. chat.Lines.Select(l => (l.Date, l.LineNumber, chat.File))], out var order, out var reason))
            {
                decided[chat] = order;
            }
            else
            {
                undecided.Add((chat, reason!));
            }
        }

        foreach (var (chat, reason) in undecided)
        {
            var shape = Shape(chat);
            var peers = decided.Where(p => Shape(p.Key) == shape).Select(p => p.Value).Distinct().ToArray();

            if (peers.Length != 1)
            {
                throw new InvalidDataException(reason);
            }

            decided[chat] = peers[0];
        }

        return decided;
    }

    /// <summary>A chat's date shape: its separator and how many digits its year has.</summary>
    private static string Shape(Chat chat)
    {
        if (chat.Lines.Count == 0)
        {
            return string.Empty;
        }

        var date = chat.Lines[0].Date;
        var separator = date.First(c => c is '.' or '/' or '-');

        return $"{separator}{date.Length - date.LastIndexOf(separator) - 1}";
    }

    /// <summary>
    /// Chat exports in the folder or one level down, recognized by name and by their first line.
    /// </summary>
    private static string[] ChatFiles(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(path, "*.txt", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(path).SelectMany(d => Directory.EnumerateFiles(d, "*.txt")))
            .Where(f => IsChatName(Path.GetFileName(f)) && StartsLikeAChat(f))
            .Order(StringComparer.Ordinal)];
    }

    private static bool IsChatName(string name) =>
        name.Equals("_chat.txt", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase);

    private static bool StartsLikeAChat(string file) =>
        File.ReadLines(file).Take(20).Select(Clean).FirstOrDefault(l => l.Length > 0) is { } first &&
        (IosHeader().IsMatch(first) || AndroidHeader().IsMatch(first));

    private static string Clean(string line) => line.TrimStart(Bom, Lrm);

    /// <summary>The chat's name, from its file or, for <c>_chat.txt</c>, from its folder.</summary>
    internal static string ChatName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);

        if (name.Equals("_chat", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileName(Path.GetDirectoryName(file)) ?? name;
        }

        foreach (var marker in new[] { "WhatsApp Chat with ", "WhatsApp Chat - ", "WhatsApp Chat mit ", "WhatsApp Chat con ", "WhatsApp Chat avec " })
        {
            var at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

            if (at >= 0)
            {
                return name[(at + marker.Length)..].Trim();
            }
        }

        return name.Trim();
    }

    /// <summary>One line that began a message, with everything up to the next one.</summary>
    private sealed class RawLine(string date, string time, string rest, int lineNumber)
    {
        public string Date { get; } = date;
        public string Time { get; } = time;
        public StringBuilder Rest { get; } = new(rest);
        public int LineNumber { get; } = lineNumber;
    }

    private sealed record Chat(string File, string Name, IReadOnlyList<RawLine> Lines);

    private static Chat Parse(string file)
    {
        var lines = new List<RawLine>();
        RawLine? current = null;
        var number = 0;

        foreach (var raw in File.ReadLines(file))
        {
            number++;
            var line = Clean(raw);
            var match = IosHeader().Match(line) is { Success: true } ios ? ios : AndroidHeader().Match(line);

            if (match.Success)
            {
                current = new RawLine(match.Groups["date"].Value, match.Groups["time"].Value, match.Groups["rest"].Value, number);
                lines.Add(current);
                continue;
            }

            if (current is null)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"Line {number} of '{file}' does not start like a WhatsApp message, and nothing came before it.");
            }

            current.Rest.Append('\n').Append(raw);
        }

        return new Chat(file, ChatName(file), lines);
    }

    /// <summary>Day-month-year, month-day-year, or year-month-day.</summary>
    internal enum Order
    {
        DayFirst,
        MonthFirst,
        YearFirst,
    }

    /// <summary>
    /// The one date order that fits every chat in the folder.
    /// </summary>
    /// <remarks>
    /// A day above twelve rules an order out. When both survive, the one that keeps each chat in
    /// sequence wins — the wrong reading of a chat that spans a month change jumps back months at a
    /// time, and backward jumps of more than a day are counted under each. A tie is refused: a
    /// chat that never says which order it uses cannot be read correctly by any rule.
    /// </remarks>
    /// <returns>
    /// False, with the reason, only when the dates read correctly both ways — the one failure a
    /// neighbouring chat can resolve. Anything else that is wrong with them throws.
    /// </returns>
    internal static bool TryDateOrder(
        IReadOnlyList<(string Date, int Line, string File)> dates, out Order order, out string? ambiguity)
    {
        ambiguity = null;
        order = DateOrderOrNull(dates, out ambiguity) ?? default;

        return ambiguity is null;
    }

    private static Order? DateOrderOrNull(IReadOnlyList<(string Date, int Line, string File)> dates, out string? ambiguity)
    {
        ambiguity = null;

        if (dates.Count == 0)
        {
            return Order.DayFirst;
        }

        var parts = dates.Select(d => (Numbers: Split(d.Date, d.File, d.Line), d.Date)).ToArray();

        if (parts.All(p => p.Numbers.First.Length == 4))
        {
            return Order.YearFirst;
        }

        if (parts.Any(p => p.Numbers.First.Length == 4))
        {
            throw new InvalidDataException("This export mixes year-first dates with others, which no single locale does.");
        }

        var dotted = parts.All(p => p.Date.Contains('.', StringComparison.Ordinal));

        var dayFirst = parts.All(p => Valid(p.Numbers.Second, p.Numbers.First));
        var monthFirst = !dotted && parts.All(p => Valid(p.Numbers.First, p.Numbers.Second));

        if (dayFirst && !monthFirst)
        {
            return Order.DayFirst;
        }

        if (monthFirst && !dayFirst)
        {
            return Order.MonthFirst;
        }

        if (!dayFirst)
        {
            throw new InvalidDataException(
                "These dates fit neither day-first nor month-first order. This is not a WhatsApp date format this reader knows.");
        }

        var backDayFirst = BackwardJumps(dates, Order.DayFirst);
        var backMonthFirst = BackwardJumps(dates, Order.MonthFirst);

        if (backDayFirst != backMonthFirst)
        {
            return backDayFirst < backMonthFirst ? Order.DayFirst : Order.MonthFirst;
        }

        ambiguity =
            "The dates in this export read correctly both day-first and month-first, and nothing in it "
            + $"says which (for example '{dates[0].Date}'). Export a longer stretch of the chat, so that a "
            + "day after the twelfth appears, and import that instead.";

        return null;
    }

    private static bool Valid(string month, string day) =>
        int.Parse(month, CultureInfo.InvariantCulture) is >= 1 and <= 12 &&
        int.Parse(day, CultureInfo.InvariantCulture) is >= 1 and <= 31;

    private static int BackwardJumps(IReadOnlyList<(string Date, int Line, string File)> dates, Order order)
    {
        var jumps = 0;
        DateTime? previous = null;
        string? file = null;

        foreach (var (date, line, f) in dates)
        {
            if (f != file)
            {
                previous = null;
                file = f;
            }

            if (!TryDate(date, order, out var day))
            {
                return int.MaxValue;
            }

            if (previous is { } p && day < p.AddDays(-1))
            {
                jumps++;
            }

            previous = day;
        }

        return jumps;
    }

    private static (string First, string Second, string Third) Split(string date, string file, int line)
    {
        var pieces = date.Split('.', '/', '-');

        if (pieces.Length != 3)
        {
            throw new InvalidDataException($"'{date}' on line {line} of '{file}' is not a date.");
        }

        return (pieces[0], pieces[1], pieces[2]);
    }

    private static bool TryDate(string date, Order order, out DateTime value)
    {
        value = default;
        var pieces = date.Split('.', '/', '-');

        if (pieces.Length != 3)
        {
            return false;
        }

        var numbers = pieces.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();

        var (year, month, day) = order switch
        {
            Order.YearFirst => (numbers[0], numbers[1], numbers[2]),
            Order.DayFirst => (numbers[2], numbers[1], numbers[0]),
            _ => (numbers[2], numbers[0], numbers[1]),
        };

        // WhatsApp began in 2009, so a two-digit year is this century.
        if (year < 100)
        {
            year += 2000;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        value = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

        return true;
    }

    private static DateTime When(RawLine line, Order order, string file)
    {
        if (!TryDate(line.Date, order, out var day))
        {
            throw new InvalidDataException($"'{line.Date}' on line {line.LineNumber} of '{file}' is not a date in {order} order.");
        }

        var time = line.Time.Replace(" ", string.Empty, StringComparison.Ordinal);
        var meridiem = string.Empty;

        var letters = time.IndexOfAny(['A', 'a', 'P', 'p']);

        if (letters >= 0)
        {
            meridiem = time[letters..].Replace(".", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
            time = time[..letters];
        }

        // Any whitespace a locale put before the AM — a no-break or narrow no-break space among them.
        time = new string([.. time.Where(c => !char.IsWhiteSpace(c))]);

        var pieces = time.Split(':', '.');
        var hour = int.Parse(pieces[0], CultureInfo.InvariantCulture);
        var minute = int.Parse(pieces[1], CultureInfo.InvariantCulture);
        var second = pieces.Length > 2 ? int.Parse(pieces[2], CultureInfo.InvariantCulture) : 0;

        if (meridiem.Length > 0)
        {
            if (hour is < 1 or > 12 || meridiem is not ("AM" or "PM"))
            {
                throw new InvalidDataException($"'{line.Time}' on line {line.LineNumber} of '{file}' is not a time.");
            }

            hour = (hour % 12) + (meridiem == "PM" ? 12 : 0);
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            throw new InvalidDataException($"'{line.Time}' on line {line.LineNumber} of '{file}' is not a time.");
        }

        return day.AddHours(hour).AddMinutes(minute).AddSeconds(second);
    }

    /// <summary>A sender and their words, or a notice the app wrote with nobody's name on it.</summary>
    private static (string? Sender, string Text, bool IsNotice) Split(RawLine line)
    {
        var rest = line.Rest.ToString();
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);

        // Android writes its notices with no sender at all.
        if (colon < 0)
        {
            return (null, rest, true);
        }

        var sender = rest[..colon];
        var text = rest[(colon + 2)..];

        // The iPhone writes them under the chat's name, marked with a left-to-right mark — the same
        // mark it puts before an attachment, which is not a notice.
        if (text.Length > 0 && text[0] == Lrm)
        {
            var body = text.TrimStart(Lrm);

            if (!IosAttachment().IsMatch(body) && !Omitted().IsMatch(body))
            {
                return (null, body, true);
            }

            text = body;
        }

        return (sender, text, false);
    }

    private void ReadChat(string exportRoot, Chat chat, Order order, NormalizedIdentity? owner, IImportSink sink)
    {
        var entries = chat.Lines
            .Select(l => (Line: l, At: When(l, order, chat.File), Parts: Split(l)))
            .ToArray();

        var senders = entries
            .Where(e => !e.Parts.IsNotice && e.Parts.Sender is not null)
            .Select(e => e.Parts.Sender!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var kind = senders.Length > 2 ? "group" : "dm";
        var threadId = $"chat/{chat.Name}";
        var roster = senders.Select(Person).ToArray();

        var thread = new NormalizedThread(threadId, kind, chat.Name, roster);
        sink.OnThread(thread);

        var folder = Path.GetDirectoryName(chat.File)!;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (line, at, (sender, text, isNotice)) in entries)
        {
            var (body, media) = isNotice ? (text, new List<NormalizedMedia>()) : Media(exportRoot, folder, text);

            if (body.EndsWith("<This message was edited>", StringComparison.Ordinal))
            {
                body = body[..^"<This message was edited>".Length].TrimEnd(' ', Lrm);
            }

            var unix = new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeSeconds();
            var who = sender is null ? null : Person(sender);

            var key = $"{unix}/{Hash($"{sender}{Separator}{text}")[..12]}";
            var occurrence = seen.GetValueOrDefault(key);
            seen[key] = occurrence + 1;

            sink.OnMessage(thread, new NormalizedMessage
            {
                Uid = $"wa/{chat.Name}/{key}/{occurrence}",
                SourceThreadId = threadId,
                Kind = isNotice ? "service" : "message",
                Sender = who,
                ServiceAction = isNotice ? "notice" : null,
                SentAtUtc = new DateTimeOffset(at, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
                SentAtUnix = unix,
                Plaintext = body,
                ContentHash = Hash($"{body}{Separator}{string.Join(Separator, media.Select(m => m.ExportPath))}"),
                RawJson = line.Rest.ToString(),
                Media = media,
            });
        }
    }

    /// <summary>An attachment on the message's first line, and whatever words came with it.</summary>
    private static (string Text, List<NormalizedMedia> Media) Media(string exportRoot, string folder, string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var first = (newline < 0 ? text : text[..newline]).TrimStart(Lrm).TrimEnd('\r');
        var caption = newline < 0 ? string.Empty : text[(newline + 1)..];

        var media = new List<NormalizedMedia>();

        if ((IosAttachment().Match(first) is { Success: true } ios ? ios : AndroidAttachment().Match(first)) is { Success: true } attached)
        {
            var file = attached.Groups["file"].Value.Trim();
            var relative = Path.GetRelativePath(exportRoot, Path.Combine(folder, file)).Replace('\\', '/');

            media.Add(new NormalizedMedia(
                relative, KindOf(file), file, Mime: null, MissingReason: null,
                StickerEmoji: null, Width: null, Height: null, DurationSeconds: null));

            return (caption, media);
        }

        if (Omitted().Match(first) is { Success: true } omitted)
        {
            var kind = omitted.Groups["kind"].Value switch
            {
                "image" => "photo",
                "video" => "video",
                "audio" => "voice",
                "sticker" => "sticker",
                "GIF" => "animation",
                _ => "file",
            };

            media.Add(new NormalizedMedia(
                first, kind, OriginalFilename: null, Mime: null,
                MissingReason: "the chat was exported without media",
                StickerEmoji: null, Width: null, Height: null, DurationSeconds: null));

            return (caption, media);
        }

        return (text, media);
    }

    /// <summary>WhatsApp's own file names say what they are: PHOTO, VID, PTT, STK and the rest.</summary>
    internal static string KindOf(string file)
    {
        var name = file.ToUpperInvariant();
        var extension = Path.GetExtension(name);

        return name switch
        {
            _ when name.Contains("STICKER", StringComparison.Ordinal) || name.StartsWith("STK-", StringComparison.Ordinal) || extension == ".WEBP" => "sticker",
            _ when name.Contains("GIF", StringComparison.Ordinal) => "animation",
            _ when name.Contains("PTT", StringComparison.Ordinal) || extension is ".OPUS" => "voice",
            _ when name.Contains("AUDIO", StringComparison.Ordinal) || name.StartsWith("AUD-", StringComparison.Ordinal) || extension is ".M4A" or ".MP3" or ".AAC" => "audio",
            _ when name.Contains("VIDEO", StringComparison.Ordinal) || name.StartsWith("VID-", StringComparison.Ordinal) || extension is ".MP4" or ".MOV" or ".3GP" => "video",
            _ when name.Contains("PHOTO", StringComparison.Ordinal) || name.StartsWith("IMG-", StringComparison.Ordinal) || extension is ".JPG" or ".JPEG" or ".PNG" or ".HEIC" => "photo",
            _ => "file",
        };
    }

    /// <summary>
    /// A sender: by number when the phone had no contact for them, by name otherwise.
    /// </summary>
    internal static NormalizedIdentity Person(string sender) =>
        PhoneNumber().IsMatch(sender) && sender.Count(char.IsAsciiDigit) >= 7
            ? new NormalizedIdentity(PlatformId, SmsBackupImporter.Normalize(sender), sender, sender, IsSynthetic: false)
            : new NormalizedIdentity(PlatformId, null, null, sender, IsSynthetic: true);

    /// <summary>
    /// Who the owner is: the user's answer, else the one sender in every chat.
    /// </summary>
    /// <remarks>
    /// The answer is matched against the senders as written, ignoring case and, for a number, its
    /// formatting — so "+1 555 000 0000" finds the sender written "+15550000000".
    /// </remarks>
    private static string? OwnerName(IReadOnlyList<Chat> chats, string? stated)
    {
        var perChat = chats.Select(c => c.Lines.Select(Split).Where(p => !p.IsNotice && p.Sender is not null)
            .Select(p => p.Sender!).ToHashSet(StringComparer.Ordinal)).ToArray();

        if (!string.IsNullOrWhiteSpace(stated))
        {
            var answer = stated.Trim();
            var normalized = PhoneNumber().IsMatch(answer) ? SmsBackupImporter.Normalize(answer) : null;

            return perChat.SelectMany(s => s).FirstOrDefault(s =>
                       s.Equals(answer, StringComparison.OrdinalIgnoreCase) ||
                       (normalized is not null && PhoneNumber().IsMatch(s) && SmsBackupImporter.Normalize(s) == normalized))
                   ?? answer;
        }

        if (perChat.Length < 2)
        {
            return null;
        }

        var common = new HashSet<string>(perChat[0], StringComparer.Ordinal);

        foreach (var set in perChat.Skip(1))
        {
            common.IntersectWith(set);
        }

        return common.Count == 1 ? common.First() : null;
    }

    /// <summary>
    /// Senders from the first lines of each chat, those in the most chats first.
    /// </summary>
    /// <remarks>
    /// Two hundred lines from at most twenty chats: detection is cheap by contract, and the owner
    /// speaks early in almost any chat worth importing.
    /// </remarks>
    private static IReadOnlyList<string> Candidates(string[] files)
    {
        var chats = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files.Take(20))
        {
            var inThisChat = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in File.ReadLines(file).Take(200))
            {
                var line = Clean(raw);
                var match = IosHeader().Match(line) is { Success: true } ios ? ios : AndroidHeader().Match(line);

                if (!match.Success)
                {
                    continue;
                }

                var (sender, _, isNotice) = Split(new RawLine(string.Empty, string.Empty, match.Groups["rest"].Value, 0));

                if (isNotice || sender is null)
                {
                    continue;
                }

                lines[sender] = lines.GetValueOrDefault(sender) + 1;

                if (inThisChat.Add(sender))
                {
                    chats[sender] = chats.GetValueOrDefault(sender) + 1;
                }
            }
        }

        return [.. chats.Keys
            .OrderByDescending(s => chats[s])
            .ThenByDescending(s => lines[s])
            .ThenBy(s => s, StringComparer.Ordinal)
            .Take(5)];
    }

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
}
