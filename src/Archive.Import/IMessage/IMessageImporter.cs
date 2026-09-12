using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Archive.Import.Sms;
using Microsoft.Data.Sqlite;

namespace Archive.Import.IMessage;

/// <summary>
/// iMessage and SMS, from the Messages database a Mac already keeps.
/// </summary>
/// <remarks>
/// <para>
/// Not an export: <c>~/Library/Messages/chat.db</c> is a plain SQLite database that Messages
/// maintains, with <c>Attachments/</c> beside it. Point at that folder. Reading it needs Full Disk
/// Access for whichever app is doing the reading, which macOS grants in System Settings; nothing
/// here can ask for it, and without it the file simply cannot be opened.
/// </para>
/// <para>
/// The database is copied before it is read — with its <c>-wal</c> and <c>-shm</c> if they are
/// there — because Messages may be running, and the newest messages live in the write-ahead log
/// until it is checkpointed. Reading the file alone would silently miss them, which is the failure
/// this project dislikes most: an import that looks complete and is not.
/// </para>
/// <para>
/// Traps, each of which this reader handles explicitly:
/// </para>
/// <list type="bullet">
///   <item><b>The text is often not in the text column.</b> Modern Messages stores it in
///   <c>attributedBody</c>, an undocumented archived object — see <see cref="TypedStreamText"/>,
///   which reads one shape and refuses the rest.</item>
///   <item><b>Timestamps are nanoseconds since 2001</b> on anything recent, and seconds on older
///   databases. Read as Unix seconds, the whole archive lands in 1970 or far in the future.</item>
///   <item><b>Tapbacks are messages.</b> A "loved" reaction is a row of its own pointing at another
///   message's guid; imported as a message it becomes a conversation full of "Loved an image".
///   They are folded into reactions on what they point at, and a removed one cancels the one it
///   removes.</item>
///   <item><b>The database does not name its owner</b> outright, but each message carries the
///   account that sent or received it (<c>p:+1555…</c>, <c>e:someone@…</c>). The commonest is the
///   owner's; when there is none, the preview asks rather than inventing one (D25).</item>
/// </list>
/// <para>
/// <b>This reader has never met a real chat.db.</b> It is written to the schema as documented by
/// the tools that read it, and D22 is the standing warning about exactly that.
/// </para>
/// </remarks>
public sealed class IMessageImporter : IPlatformImporter
{
    public const string PlatformId = "imessage";

    /// <summary>Apple's epoch: 1 January 2001, UTC.</summary>
    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Tapbacks: 2000-2005 add one, 3000-3005 take the same one away.</summary>
    private const int FirstReaction = 2000;
    private const int LastReaction = 2005;
    private const int FirstRemoval = 3000;
    private const int LastRemoval = 3005;

    public string Platform => PlatformId;

    public string DisplayName => "iMessage (Messages on macOS)";

    public ImportDetection Detect(string path)
    {
        var database = DatabaseIn(path);

        if (database is null)
        {
            return ImportDetection.No;
        }

        try
        {
            if (!LooksLikeMessages(database))
            {
                return ImportDetection.No;
            }
        }
        catch (SqliteException)
        {
            // A locked or unreadable file. Saying "not mine" lets the folder be offered to other
            // readers rather than failing detection for everyone.
            return ImportDetection.No;
        }

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: null,
            AccountName: null,
            FileCount: 1,
            Note: "Read straight from the Messages database, which the app needs Full Disk Access to "
                + "open. Tapbacks are stored as reactions rather than as messages, and the database "
                + "does not state which account is yours — it is read from your own messages.",
            AccountIdIsGuess: true);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var database = DatabaseIn(path)
            ?? throw new InvalidDataException(
                $"'{path}' holds no chat.db. Point at the folder containing it — on a Mac that is "
                + "~/Library/Messages, and reading it needs Full Disk Access.");

        var folder = Path.GetDirectoryName(Path.GetFullPath(database))!;

        using var snapshot = new Snapshot(database);
        using var connection = new SqliteConnection($"Data Source={snapshot.DatabasePath}");
        connection.Open();

        var owner = Owner(connection, folder, options?.OwnerAccountId);
        sink.OnOwner(owner);

        var handles = Handles(connection);
        var reactions = Reactions(connection, owner, handles);

        foreach (var chat in Chats(connection, handles))
        {
            ReadChat(connection, chat, owner, handles, reactions, folder, sink);
        }
    }

    /// <summary>The Messages database in this folder, or the file itself if that is what was named.</summary>
    private static string? DatabaseIn(string path)
    {
        if (File.Exists(path) && Path.GetFileName(path) == "chat.db")
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var candidate = Path.Combine(path, "chat.db");

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Whether this really is Messages' database, rather than some other file called chat.db.
    /// </summary>
    /// <remarks>
    /// Opened read-only and asked only for its table names, which is one page of the file — the
    /// detection contract's "a few kilobytes" in a form SQLite understands.
    /// </remarks>
    private static bool LooksLikeMessages(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('message', 'chat', 'handle', 'chat_message_join');";

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 4;
    }

    /// <summary>
    /// Whose Mac this was: what the user said, the account their own messages carry, or a
    /// placeholder recorded as the guess it is (D25).
    /// </summary>
    private static NormalizedIdentity Owner(SqliteConnection connection, string folder, string? stated)
    {
        if (!string.IsNullOrWhiteSpace(stated))
        {
            return new NormalizedIdentity(
                PlatformId, Account(stated), stated.Trim(), "You", IsSynthetic: false);
        }

        using var command = connection.CreateCommand();

        // The account a message was sent from is written on every row; the commonest across the
        // owner's own messages is the account this Mac is signed in as.
        command.CommandText = """
            SELECT account FROM message
            WHERE is_from_me = 1 AND account IS NOT NULL AND account <> ''
            GROUP BY account
            ORDER BY count(*) DESC
            LIMIT 1;
            """;

        if (command.ExecuteScalar() is string account && account.Length > 0)
        {
            var id = Account(account);

            return new NormalizedIdentity(PlatformId, id, id, "You", IsSynthetic: false);
        }

        return new NormalizedIdentity(
            PlatformId,
            "folder:" + new DirectoryInfo(folder).Name,
            Handle: null,
            "You (account not identified)",
            IsSynthetic: true);
    }

    /// <summary>
    /// An account or handle as an identity id.
    /// </summary>
    /// <remarks>
    /// Messages writes its own accounts prefixed — <c>p:</c> for a phone number, <c>e:</c> for an
    /// Apple ID — while a handle is written bare. Both end up as the same id for the same person,
    /// and a number keeps only its formatting removed: no country code is ever added, because which
    /// country a local number belonged to is not in this database (the SMS reader's rule).
    /// </remarks>
    internal static string Account(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.StartsWith("p:", StringComparison.Ordinal) || trimmed.StartsWith("e:", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        // A ";"-suffixed account — "p:+15551234567;-;…" — names the same number.
        var semicolon = trimmed.IndexOf(';', StringComparison.Ordinal);

        if (semicolon > 0)
        {
            trimmed = trimmed[..semicolon];
        }

        return trimmed.Contains('@', StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : SmsBackupImporter.Normalize(trimmed);
    }

    private static Dictionary<long, NormalizedIdentity> Handles(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ROWID, id FROM handle;";

        var handles = new Dictionary<long, NormalizedIdentity>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = Account(reader.GetString(1));

            handles[reader.GetInt64(0)] = new NormalizedIdentity(
                PlatformId, id, id, id, IsSynthetic: false);
        }

        return handles;
    }

    private sealed record ChatRow(long Id, string Guid, string Kind, string? Title, IReadOnlyList<NormalizedIdentity> People);

    private static List<ChatRow> Chats(SqliteConnection connection, Dictionary<long, NormalizedIdentity> handles)
    {
        var chats = new List<ChatRow>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ROWID, guid, style, display_name, chat_identifier FROM chat;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var style = reader.IsDBNull(2) ? 45 : reader.GetInt64(2);
                var name = reader.IsDBNull(3) || reader.GetString(3).Length == 0 ? null : reader.GetString(3);

                chats.Add(new ChatRow(
                    reader.GetInt64(0),
                    reader.GetString(1),

                    // 43 is a group, 45 a conversation with one person. Anything else is treated as
                    // a group, which is the reading that loses least if Apple adds a style.
                    style == 45 ? "dm" : "group",
                    name ?? (reader.IsDBNull(4) ? null : reader.GetString(4)),
                    []));
            }
        }

        // D28: who was in the room, which is not the same question as who spoke. The per-person
        // view finds someone's direct threads through this.
        var rosters = new Dictionary<long, List<NormalizedIdentity>>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT chat_id, handle_id FROM chat_handle_join;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (!handles.TryGetValue(reader.GetInt64(1), out var person))
                {
                    continue;
                }

                if (!rosters.TryGetValue(reader.GetInt64(0), out var people))
                {
                    people = [];
                    rosters[reader.GetInt64(0)] = people;
                }

                people.Add(person);
            }
        }

        return [.. chats.Select(c => c with { People = rosters.GetValueOrDefault(c.Id, []) })];
    }

    /// <summary>
    /// Tapbacks, gathered before anything is read, keyed by the message they point at.
    /// </summary>
    /// <remarks>
    /// They arrive as ordinary rows and have to be folded into the message they are about, so they
    /// are collected first: a reaction usually sits after its target, and the committer only writes
    /// reactions when the message itself is new.
    /// </remarks>
    private static Dictionary<string, List<NormalizedReaction>> Reactions(
        SqliteConnection connection, NormalizedIdentity owner, Dictionary<long, NormalizedIdentity> handles)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT associated_message_guid, associated_message_type, is_from_me, handle_id, date
            FROM message
            WHERE associated_message_type BETWEEN 2000 AND 3005
              AND associated_message_guid IS NOT NULL
            ORDER BY date;
            """;

        // Keyed by target and reactor: a second tapback of the same kind replaces the first, and a
        // removal cancels it, which is what Messages itself shows.
        var live = new Dictionary<(string Target, string Actor, string Emoji), (bool On, long Date)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var target = Target(reader.GetString(0));
            var type = reader.GetInt64(1);
            var actor = reader.GetInt64(2) == 1
                ? owner
                : handles.GetValueOrDefault(reader.IsDBNull(3) ? 0 : reader.GetInt64(3));

            if (actor is null || target is null)
            {
                continue;
            }

            var removal = type is >= FirstRemoval and <= LastRemoval;
            var emoji = Tapback(removal ? type - 1000 : type);

            if (emoji is null)
            {
                continue;
            }

            live[(target, actor.SourceIdentityId!, emoji)] = (!removal, reader.GetInt64(4));
        }

        var reactions = new Dictionary<string, List<NormalizedReaction>>(StringComparer.Ordinal);

        foreach (var (key, value) in live.Where(entry => entry.Value.On))
        {
            var actor = key.Actor == owner.SourceIdentityId
                ? owner
                : handles.Values.FirstOrDefault(h => h.SourceIdentityId == key.Actor);

            if (!reactions.TryGetValue(key.Target, out var list))
            {
                list = [];
                reactions[key.Target] = list;
            }

            list.Add(new NormalizedReaction(
                key.Emoji, CustomEmojiId: null, actor, 1, When(value.Date).ToString("O", CultureInfo.InvariantCulture)));
        }

        return reactions;
    }

    /// <summary>
    /// The message a tapback is about.
    /// </summary>
    /// <remarks>
    /// Written as <c>p:0/GUID</c> for a message with several parts and <c>bp:GUID</c> for some
    /// attachments; both name the same message.
    /// </remarks>
    private static string? Target(string associated)
    {
        var slash = associated.LastIndexOf('/');

        if (slash >= 0)
        {
            return associated[(slash + 1)..];
        }

        var colon = associated.LastIndexOf(':');

        return colon >= 0 ? associated[(colon + 1)..] : associated;
    }

    /// <summary>The six tapbacks, as the emoji each one means.</summary>
    private static string? Tapback(long type) => type switch
    {
        2000 => "❤️",
        2001 => "👍",
        2002 => "👎",
        2003 => "😂",
        2004 => "‼️",
        2005 => "❓",
        _ => null,
    };

    private static void ReadChat(
        SqliteConnection connection,
        ChatRow chat,
        NormalizedIdentity owner,
        Dictionary<long, NormalizedIdentity> handles,
        Dictionary<string, List<NormalizedReaction>> reactions,
        string folder,
        IImportSink sink)
    {
        var people = new List<NormalizedIdentity>(chat.People);
        var thread = new NormalizedThread(chat.Guid, chat.Kind, chat.Title, people);
        var announced = false;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.ROWID, m.guid, m.text, m.attributedBody, m.date, m.is_from_me, m.handle_id,
                   m.service, m.item_type, m.associated_message_type, m.cache_has_attachments
            FROM message m
            JOIN chat_message_join j ON j.message_id = m.ROWID
            WHERE j.chat_id = $chat
            ORDER BY m.date, m.ROWID;
            """;
        command.Parameters.AddWithValue("$chat", chat.Id);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var associated = reader.IsDBNull(9) ? 0 : reader.GetInt64(9);

            // A tapback is not a message. Imported as one, a conversation fills up with "Loved an
            // image" and the reaction it actually was is lost.
            if (associated is >= FirstReaction and <= LastRemoval)
            {
                continue;
            }

            var id = reader.GetInt64(0);
            var guid = reader.GetString(1);
            var text = Text(reader, guid);
            var isFromMe = reader.GetInt64(5) == 1;
            var itemType = reader.IsDBNull(8) ? 0 : reader.GetInt64(8);

            var sender = isFromMe
                ? owner
                : handles.GetValueOrDefault(reader.IsDBNull(6) ? 0 : reader.GetInt64(6));

            var at = When(reader.GetInt64(4));
            var media = Attachments(connection, id, folder);

            if (!announced)
            {
                announced = true;
                sink.OnThread(thread);
            }

            var action = itemType == 0 ? null : ServiceAction(itemType);

            sink.OnMessage(thread, new NormalizedMessage
            {
                Uid = $"im/{chat.Guid}/{guid}",
                SourceThreadId = chat.Guid,
                Kind = action is null ? "message" : "service",
                Sender = sender,
                ServiceAction = action,
                SentAtUtc = at.ToString("O", CultureInfo.InvariantCulture),
                SentAtUnix = at.ToUnixTimeSeconds(),
                Plaintext = text,
                ContentHash = Hash(text, action, media),
                Media = media,
                Reactions = reactions.GetValueOrDefault(guid, []),

                // No raw row is kept: the database is the user's own file and stays where it is,
                // so a parser gap is fixed by reading it again rather than from a copy in here.
                RawJson = null,
            });
        }
    }

    /// <summary>The words of a message, from whichever column this version of Messages used.</summary>
    private static string Text(SqliteDataReader reader, string guid)
    {
        if (!reader.IsDBNull(2) && reader.GetString(2) is { Length: > 0 } plain)
        {
            return plain;
        }

        if (reader.IsDBNull(3))
        {
            return string.Empty;
        }

        var blob = (byte[])reader.GetValue(3);

        try
        {
            return TypedStreamText.Read(blob) ?? string.Empty;
        }
        catch (InvalidDataException ex)
        {
            // Says which message, so the file can be looked at; never what it said (P6).
            throw new InvalidDataException($"iMessage {guid}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// What kind of event a non-message row is.
    /// </summary>
    /// <remarks>
    /// Only the ones whose meaning is established. An unfamiliar item_type becomes a named service
    /// message rather than a guess at what happened or a silently dropped row.
    /// </remarks>
    private static string ServiceAction(long itemType) => itemType switch
    {
        1 => "participants_changed",
        2 => "group_name_changed",
        3 => "left_group",
        4 => "shared_location",
        5 => "shared_screen",
        6 => "group_photo_changed",
        _ => $"item_type_{itemType.ToString(CultureInfo.InvariantCulture)}",
    };

    private static List<NormalizedMedia> Attachments(SqliteConnection connection, long messageId, string folder)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.filename, a.mime_type, a.transfer_name, a.total_bytes, a.is_sticker
            FROM attachment a
            JOIN message_attachment_join j ON j.attachment_id = a.ROWID
            WHERE j.message_id = $message
            ORDER BY a.ROWID;
            """;
        command.Parameters.AddWithValue("$message", messageId);

        var media = new List<NormalizedMedia>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var stored = reader.GetString(0);
            var mime = reader.IsDBNull(1) ? null : reader.GetString(1);
            var name = reader.IsDBNull(2) ? Path.GetFileName(stored) : reader.GetString(2);
            var sticker = !reader.IsDBNull(4) && reader.GetInt64(4) == 1;

            var relative = Relative(stored, folder);

            media.Add(new NormalizedMedia(
                relative,
                sticker ? "sticker" : KindOf(mime),
                name,
                mime,
                MissingReason: File.Exists(Path.Combine(folder, relative)) ? null : "not in the Attachments folder",
                StickerEmoji: null,
                Width: null,
                Height: null,
                DurationSeconds: null));
        }

        return media;
    }

    /// <summary>
    /// An attachment's path, relative to the folder the user pointed at.
    /// </summary>
    /// <remarks>
    /// Messages stores <c>~/Library/Messages/Attachments/…</c>, which is only meaningful on the Mac
    /// it was written on. What survives being copied to another machine is the part below the
    /// Messages folder, which is what the media store is given.
    /// </remarks>
    private static string Relative(string stored, string folder)
    {
        var path = stored.Replace('\\', '/');
        var marker = path.IndexOf("Attachments/", StringComparison.OrdinalIgnoreCase);

        if (marker >= 0)
        {
            return path[marker..];
        }

        var full = Path.GetFullPath(path);

        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, full).Replace('\\', '/')
            : path;
    }

    private static string KindOf(string? mime) => mime switch
    {
        null => "file",
        "image/gif" => "animation",
        _ when mime.StartsWith("image/", StringComparison.Ordinal) => "photo",
        _ when mime.StartsWith("video/", StringComparison.Ordinal) => "video",
        "audio/amr" or "audio/x-m4a" => "voice",
        _ when mime.StartsWith("audio/", StringComparison.Ordinal) => "audio",
        _ => "file",
    };

    /// <summary>
    /// A Messages timestamp, in whichever unit this database uses.
    /// </summary>
    /// <remarks>
    /// Nanoseconds since 2001 on anything from High Sierra onward, seconds before that. The two are
    /// told apart by size rather than by a version check, and a value that is neither is refused:
    /// picking the wrong one puts an entire archive decades away from where it happened.
    /// </remarks>
    internal static DateTimeOffset When(long date)
    {
        if (date == 0)
        {
            return AppleEpoch;
        }

        // Anything past a billion seconds after 2001 is the year 2033 — so a value that large is
        // nanoseconds, and one that small is seconds.
        var seconds = Math.Abs(date) > 1_000_000_000_000L ? date / 1_000_000_000L : date;

        if (seconds is < -1_000_000_000L or > 4_000_000_000L)
        {
            throw new InvalidDataException(
                $"An iMessage row has date {date}, which is neither seconds nor nanoseconds since 2001.");
        }

        return AppleEpoch.AddSeconds(seconds);
    }

    private static string Hash(string text, string? action, IReadOnlyList<NormalizedMedia> media)
    {
        const char Separator = (char)31;

        var builder = new StringBuilder(text).Append(Separator).Append(action).Append(Separator);

        foreach (var item in media)
        {
            builder.Append(item.ExportPath).Append(Separator);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// A copy of the database, with its write-ahead log, read instead of the original.
    /// </summary>
    /// <remarks>
    /// Messages is usually running, and SQLite keeps recent writes in <c>-wal</c> until they are
    /// checkpointed. Opening the live file read-only also needs to write <c>-shm</c>, which is not
    /// something to do to somebody's Messages database. Copying all three and opening the copy
    /// gives the recent messages and touches nothing.
    /// </remarks>
    private sealed class Snapshot : IDisposable
    {
        private readonly string _directory;

        internal Snapshot(string database)
        {
            _directory = Path.Combine(Path.GetTempPath(), "ahistory-imessage", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);

            // Not called Path: a property of that name inside this class hides System.IO.Path, and
            // every path expression in here would then be resolving against a string.
            DatabasePath = Path.Combine(_directory, "chat.db");

            File.Copy(database, DatabasePath);

            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(database + suffix))
                {
                    File.Copy(database + suffix, DatabasePath + suffix);
                }
            }
        }

        internal string DatabasePath { get; }

        public void Dispose()
        {
            using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
            {
                // Scoped to this connection string: ClearAllPools is process-wide and would close
                // handles other work is using.
                SqliteConnection.ClearPool(connection);
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory; the OS reclaims it.
            }
        }
    }
}
