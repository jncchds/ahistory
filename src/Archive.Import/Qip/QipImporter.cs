using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Archive.Import.Qip;

/// <summary>
/// QIP and QIP Infium history files (<c>.qhf</c>).
/// </summary>
/// <remarks>
/// <para>
/// A closed binary format from a dead Russian ICQ client, with no specification from its authors.
/// The layout here follows the reverse engineering published in
/// <see href="https://github.com/MolinRE/QIParser">MolinRE/QIParser</see>, which is the only
/// description of it that exists.
/// </para>
/// <para>
/// One file per contact, big-endian throughout:
/// </para>
/// <code>
/// header   0x00  3      "QHF"
///          0x03  byte   version, 3 in every file seen
///          0x04  int32  bytes after this field — the file is 8 longer
///          0x22  int32  message count
///          0x2C  int16  UIN length,  then the UIN
///                int16  nickname length, then the nickname (the contact's, not the owner's)
///
/// message  0x00  int16  signature, always 1
///          0x02  int32  bytes after this field — the block is 6 longer
///          0x06  int16  field id, always 1
///          0x08  int16  field length, always 4
///          0x0A  int32  message id
///          0x0E  int16  field id, always 2
///          0x10  int16  field length, always 4
///          0x12  int32  unix timestamp
///          0x16  int16  field id, always 3
///          0x18  int16  field length, always 3
///          0x1A  byte   1 when sent by the account owner
///          0x1B  byte   always 0
///          0x1C  byte   message type (1 online, 13 offline, 5/14 authorization)
///          0x1D  int16  field id, always 4
///          0x1F  int32  message length
///          0x23         message bytes
/// </code>
/// <para>
/// Both size fields measure what follows them rather than the whole of what they introduce, which
/// is the single thing that made every real file unreadable at first. The blocks are really
/// id/length/value triples; the offsets above are constant only because every field so far has
/// had a constant length, and the validation below is what would catch that changing.
/// </para>
/// <para>
/// Message text is UTF-8 obfuscated byte by byte with <c>b = 255 - b - i - 1</c>, which is its own
/// inverse.
/// </para>
/// <para>
/// <b>Everything here is validated on the way through</b> — the signature, every block marker, the
/// block size against the file, the message length against its block, and the timestamp against a
/// plausible range. The layout above has gaps nobody has explained, so a wrong assumption is
/// likely; the point of the checks is that a wrong assumption stops the import at a named byte
/// offset instead of filling an archive with mojibake and messages dated 1970.
/// </para>
/// </remarks>
public sealed class QipImporter : IPlatformImporter
{
    public const string PlatformId = "qip";

    /// <summary>
    /// Bytes before the region the header's size field measures: <c>"QHF"</c>, the version byte,
    /// and the size field itself.
    /// </summary>
    private const int HeaderPrefixSize = 8;

    /// <summary>
    /// Bytes of a message block that its own size field does not count: the signature and the
    /// size field.
    /// </summary>
    private const int BlockPrefixSize = 6;

    /// <summary>What a conversation with yourself is called, matching Telegram's own (D6).</summary>
    public const string SavedMessagesTitle = "Saved messages";

    /// <summary>QIP predates ICQ's decline; anything outside this is a misread field.</summary>
    private static readonly DateTimeOffset Earliest = new(1996, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string Platform => PlatformId;

    public string DisplayName => "QIP / QIP Infium";

    public ImportDetection Detect(string path)
    {
        var files = HistoryFiles(path);
        var archived = ArchivedFiles(path);

        if (files.Length == 0)
        {
            // A folder of .ahf and nothing else is a QIP folder that this reader cannot read.
            // Saying so beats "this does not look like an export this app can read", which sends
            // someone looking for the wrong folder.
            return archived.Length == 0
                ? ImportDetection.No
                : new ImportDetection(
                    ImportConfidence.Possible,
                    FileCount: archived.Length,
                    Note: ArchivedNote(archived.Length));
        }

        // Confirm the signature rather than trusting the extension: .qhf is not a well-known
        // suffix and something else could be using it.
        foreach (var file in files)
        {
            if (!HasSignature(file))
            {
                return ImportDetection.No;
            }
        }

        var owners = OwnerUins(files);
        var owner = owners.Count == 1 ? owners[0] : null;

        var note = "The QIP format was never published; this reader follows a community reverse "
            + "engineering of it and stops rather than guessing if a file does not match.";

        if (archived.Length > 0)
        {
            note += " " + ArchivedNote(archived.Length);
        }

        if (owner is null)
        {
            note += " These files do not say which UIN is yours — a .qhf names only the contact — "
                + "so tell the app, or your own account becomes a placeholder that will not line up "
                + "with you on any other platform.";
        }

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: owner,
            AccountName: owner is null ? null : $"QIP {owner}",
            FileCount: files.Length,
            Note: note,
            // Always a guess: it comes from a folder name, never from the file.
            AccountIdIsGuess: true,
            AccountCandidates: owners);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var files = HistoryFiles(path);

        if (files.Length == 0)
        {
            var archived = ArchivedFiles(path);

            throw new InvalidDataException(
                archived.Length > 0
                    ? $"'{path}' contains no .qhf history files, but {archived.Length} .ahf file(s). "
                        + ArchivedNote(archived.Length)
                    : $"'{path}' contains no .qhf history files.");
        }

        var owner = Owner(path, files, options?.OwnerAccountId);

        sink.OnOwner(owner);

        foreach (var file in files)
        {
            ReadHistory(file, owner, sink);
        }
    }

    /// <summary>
    /// Who "me" is, in order of how much the answer is worth trusting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user's own answer first, then the folder layout, then a placeholder. Only the first two
    /// produce an identity that can ever line up with the same person arriving from another
    /// platform, which is why the placeholder is flagged as the guess it is: the merge UI lists it
    /// among the accounts identified by name, and it can be detached.
    /// </para>
    /// <para>
    /// The placeholder is per-export rather than a single global "self". Two bare piles from two
    /// different accounts are two different people's halves of a conversation, and collapsing them
    /// onto one identity would attribute one account's messages to the other.
    /// </para>
    /// </remarks>
    private static NormalizedIdentity Owner(string path, string[] files, string? stated)
    {
        if (!string.IsNullOrWhiteSpace(stated))
        {
            return new NormalizedIdentity(
                PlatformId, stated, Handle: null, $"QIP {stated}", IsSynthetic: false);
        }

        var owners = OwnerUins(files);

        if (owners.Count == 1)
        {
            return new NormalizedIdentity(
                PlatformId, owners[0], Handle: null, $"QIP {owners[0]}", IsSynthetic: false);
        }

        if (owners.Count > 1)
        {
            // §9 and D13: a save is one person's archive. Two profiles under one folder is either
            // two of your accounts or somebody else's history, and the importer cannot tell which
            // — so it says what it found instead of picking one and attributing the rest to it.
            throw new InvalidDataException(
                $"'{path}' holds histories for more than one account ({string.Join(", ", owners)}). "
                + "Import one profile folder at a time; an archive belonging to someone else "
                + "belongs in its own save.");
        }

        var placeholder = "folder:" + new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar)).Name;

        return new NormalizedIdentity(
            PlatformId, placeholder, Handle: null, "QIP (account not identified)", IsSynthetic: true);
    }

    private static string ArchivedNote(int count) =>
        $"{count} .ahf file(s) here are QIP's archived history, which this reader does not read — "
        + "its layout has never been confirmed against real files, and guessing at one is how the "
        + "first version of this reader was written. Un-archive them in QIP to import them.";

    private static string[] HistoryFiles(string path) => FilesWithExtension(path, ".qhf");

    private static string[] ArchivedFiles(string path) => FilesWithExtension(path, ".ahf");

    private static string[] FilesWithExtension(string path, string extension)
    {
        if (File.Exists(path))
        {
            return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? [path] : [];
        }

        return Directory.Exists(path)
            ? [.. Directory.EnumerateFiles(path, "*" + extension, SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)]
            : [];
    }

    private static bool HasSignature(string file)
    {
        using var stream = File.OpenRead(file);

        Span<byte> head = stackalloc byte[3];

        return stream.Read(head) == 3 && head[0] == (byte)'Q' && head[1] == (byte)'H' && head[2] == (byte)'F';
    }

    /// <summary>
    /// Every account UIN the folder layout names, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QIP stores histories under <c>&lt;profile&gt;\&lt;own UIN&gt;\History\&lt;contact&gt;.qhf</c>, and a
    /// <c>.qhf</c> names only the *contact*, so that numeric folder is the only thing in an export
    /// that says who "me" is.
    /// </para>
    /// <para>
    /// It is matched exactly — the numeric directory whose child is called <c>History</c> — and
    /// not by looking for any all-digit folder name nearby. The looser version took the first
    /// digits it met walking up four levels, so <c>backup\2009\*.qhf</c> made the owner "2009",
    /// and a contact's own folder could be picked instead. Both attach a real contact to you as
    /// the archive's owner, which §1 calls out as the merge that poisons everything downstream —
    /// and it happened with no confirmation step anywhere.
    /// </para>
    /// <para>
    /// More than one answer is not resolved here. It means the folder holds more than one profile,
    /// which is a question for the user (D13), not something to average.
    /// </para>
    /// </remarks>
    private static List<string> OwnerUins(string[] files)
    {
        var owners = new List<string>();

        foreach (var file in files)
        {
            var history = new DirectoryInfo(Path.GetDirectoryName(file)!);

            // Walk up to the folder literally named History; the file may sit in a subfolder of it.
            while (history is not null && !history.Name.Equals("History", StringComparison.OrdinalIgnoreCase))
            {
                history = history.Parent;
            }

            var profile = history?.Parent;

            if (profile is not null &&
                profile.Name.Length > 0 &&
                profile.Name.All(char.IsAsciiDigit) &&
                !owners.Contains(profile.Name, StringComparer.Ordinal))
            {
                owners.Add(profile.Name);
            }
        }

        return owners;
    }

    private void ReadHistory(string file, NormalizedIdentity owner, IImportSink sink)
    {
        var bytes = File.ReadAllBytes(file);

        if (bytes.Length < 0x30 || bytes[0] != 'Q' || bytes[1] != 'H' || bytes[2] != 'F')
        {
            throw new InvalidDataException($"'{file}' does not start with the QHF signature.");
        }

        var declaredSize = ReadInt32(bytes, 0x04, file);

        // The header states the length of everything after the 8-byte prefix — the signature, the
        // version byte and this field itself are not counted. Real QIP Infium files are all
        // consistently 8 bytes longer than this number; reading it as the whole file's length
        // rejected every one of them as truncated.
        //
        // Disagreement still means the layout is not what we think it is, and every offset after
        // this one would then be read from the wrong place, so the check stays — corrected.
        if (declaredSize != bytes.Length - HeaderPrefixSize)
        {
            throw new InvalidDataException(
                $"'{file}' declares {declaredSize} bytes after its header but has "
                + $"{bytes.Length - HeaderPrefixSize}. "
                + "The file is truncated, or this is not the QHF layout this reader knows.");
        }

        var expectedMessages = ReadInt32(bytes, 0x22, file);

        var uinLength = ReadInt16(bytes, 0x2C, file);
        var uin = ReadAscii(bytes, 0x2E, uinLength, file);

        var nicknameOffset = 0x2E + uinLength;
        var nicknameLength = ReadInt16(bytes, nicknameOffset, file);
        var nickname = ReadUtf8(bytes, nicknameOffset + 2, nicknameLength, file);

        // A file whose header UIN is the owner's own is not a conversation with anybody: it is
        // either messages sent to yourself (ICQ let you add your own UIN as a contact) or
        // authorization traffic the client filed under your account. Both are yours, so it becomes
        // one 'saved' thread — the same kind Telegram's Saved Messages use (decisions.md D6) —
        // rather than a 'dm' titled with your own nickname, which is indistinguishable in the
        // schema from a real conversation and reads in the UI as a chat with yourself.
        //
        // The owner identity is reused rather than a second one built for the same account: they
        // would collide on (platform, source_identity_id) anyway, and the survivor of that
        // collision is whichever display name was written first.
        var isSelf = string.Equals(uin, owner.SourceIdentityId, StringComparison.Ordinal);

        var contact = isSelf
            ? owner
            : new NormalizedIdentity(
                PlatformId, uin, Handle: null,
                string.IsNullOrWhiteSpace(nickname) ? uin : nickname, IsSynthetic: false);

        // One file is one conversation, and QIP had no group chats worth the name.
        var thread = isSelf
            ? new NormalizedThread(uin, "saved", SavedMessagesTitle)
            : new NormalizedThread(uin, "dm", contact.DisplayName);

        sink.OnThread(thread);

        var offset = nicknameOffset + 2 + nicknameLength;
        var read = 0;

        while (offset < bytes.Length)
        {
            offset = ReadMessage(bytes, offset, file, uin, owner, contact, thread, sink);
            read++;
        }

        // The header counts them. A mismatch means blocks were walked wrongly, which would show
        // up as silently missing history rather than as an error.
        if (read != expectedMessages)
        {
            throw new InvalidDataException(
                $"'{file}' declares {expectedMessages} messages but {read} were read. "
                + "The block layout does not match this reader.");
        }
    }

    private int ReadMessage(
        byte[] bytes,
        int start,
        string file,
        string contactUin,
        NormalizedIdentity owner,
        NormalizedIdentity contact,
        NormalizedThread thread,
        IImportSink sink)
    {
        var signature = ReadInt16(bytes, start, file);

        if (signature != 1)
        {
            throw new InvalidDataException(
                $"'{file}' has {signature} where a message block signature (1) was expected, at byte {start}.");
        }

        // Like the header's, this length measures what follows it, not the whole block: the
        // signature and the size field are not counted.
        var blockSize = ReadInt32(bytes, start + 0x02, file);
        var blockEnd = start + BlockPrefixSize + blockSize;

        if (blockSize <= 0 || blockEnd > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{file}' declares a {blockSize}-byte message block at byte {start}, which runs past the file.");
        }

        // The offsets below are only constant while every field keeps the length it has always
        // had. Checking each id/length pair means a block shaped differently stops here, naming
        // the byte, instead of being read as a message assembled from the wrong fields.
        RequireField(bytes, start + 0x06, id: 1, length: 4, file);
        RequireField(bytes, start + 0x0E, id: 2, length: 4, file);
        RequireField(bytes, start + 0x16, id: 3, length: 3, file);

        // Field 4 is the odd one: its id is an int16 like the rest, but its length is an int32,
        // read below as the message length.
        RequireFieldId(bytes, start + 0x1D, id: 4, file);

        var messageId = ReadInt32(bytes, start + 0x0A, file);
        var timestamp = ReadInt32(bytes, start + 0x12, file);

        // The third field is three bytes: direction, a byte that is always zero, then the message
        // type. Not the int16 at +0x06 — that is a field *id* and is 1 in every block ever seen,
        // so classifying on it marked every service message as ordinary chat.
        var outgoing = bytes[start + 0x1A] != 0;
        var messageType = bytes[start + 0x1C];
        var length = ReadInt32(bytes, start + 0x1F, file);

        if (length < 0 || start + 0x23 + length > blockEnd)
        {
            throw new InvalidDataException(
                $"'{file}' declares a {length}-byte message at byte {start}, which runs past its own block.");
        }

        var at = DateTimeOffset.FromUnixTimeSeconds(timestamp);

        if (at < Earliest || at > DateTimeOffset.UtcNow.AddDays(1))
        {
            throw new InvalidDataException(
                $"'{file}' has a message dated {at:yyyy-MM-dd} at byte {start}, which is not a plausible "
                + "QIP timestamp. The block layout does not match this reader.");
        }

        var text = Decode(bytes.AsSpan(start + 0x23, length));

        sink.OnMessage(thread, new NormalizedMessage
        {
            // The message id is only unique within its file, so the contact's UIN scopes it.
            Uid = $"qip/{contactUin}/{messageId.ToString(CultureInfo.InvariantCulture)}",
            SourceThreadId = contactUin,
            Kind = messageType is 5 or 14 ? "service" : "message",
            Sender = outgoing ? owner : contact,
            ServiceAction = messageType switch
            {
                5 => "authorization_request",
                14 => "authorization_accepted",
                _ => null,
            },
            SentAtUtc = at.ToString("O"),
            SentAtUnix = at.ToUnixTimeSeconds(),
            Plaintext = text,
            ContentHash = ContentHash(text),
        });

        return blockEnd;
    }

    /// <summary>
    /// Undoes QIP's byte obfuscation.
    /// </summary>
    /// <remarks>
    /// <c>b = 255 - b - i - 1</c>, where <c>i</c> is the index within the message. It is its own
    /// inverse, which is presumably why they used it. Not encryption, and never was.
    /// </remarks>
    internal static string Decode(ReadOnlySpan<byte> encoded)
    {
        var decoded = new byte[encoded.Length];

        for (var i = 0; i < encoded.Length; i++)
        {
            decoded[i] = (byte)(255 - encoded[i] - i - 1);
        }

        return Encoding.UTF8.GetString(decoded);
    }

    /// <summary>Obfuscates text the way QIP stored it. Used to build test fixtures.</summary>
    internal static byte[] Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var encoded = new byte[bytes.Length];

        for (var i = 0; i < bytes.Length; i++)
        {
            encoded[i] = (byte)(255 - bytes[i] - i - 1);
        }

        return encoded;
    }

    /// <summary>Checks one id/length marker pair inside a message block.</summary>
    private static void RequireField(byte[] bytes, int offset, int id, int length, string file)
    {
        RequireFieldId(bytes, offset, id, file);

        var actual = ReadInt16(bytes, offset + 2, file);

        if (actual != length)
        {
            throw new InvalidDataException(
                $"'{file}' gives field {id} at byte {offset} a length of {actual}, not {length}. "
                + "The block layout does not match this reader.");
        }
    }

    private static void RequireFieldId(byte[] bytes, int offset, int id, string file)
    {
        var actual = ReadInt16(bytes, offset, file);

        if (actual != id)
        {
            throw new InvalidDataException(
                $"'{file}' has field id {actual} at byte {offset} where {id} was expected. "
                + "The block layout does not match this reader.");
        }
    }

    private static int ReadInt32(byte[] bytes, int offset, string file)
    {
        Require(bytes, offset, 4, file);

        return BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
    }

    private static int ReadInt16(byte[] bytes, int offset, string file)
    {
        Require(bytes, offset, 2, file);

        return BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, 2));
    }

    private static string ReadAscii(byte[] bytes, int offset, int length, string file)
    {
        Require(bytes, offset, length, file);

        return Encoding.ASCII.GetString(bytes, offset, length);
    }

    private static string ReadUtf8(byte[] bytes, int offset, int length, string file)
    {
        Require(bytes, offset, length, file);

        return Encoding.UTF8.GetString(bytes, offset, length);
    }

    private static void Require(byte[] bytes, int offset, int length, string file)
    {
        if (offset < 0 || length < 0 || offset + length > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{file}' ends before byte {offset + length}. The file is truncated, or the layout "
                + "does not match this reader.");
        }
    }

    private static string ContentHash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}
