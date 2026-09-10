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

    /// <summary>QIP predates ICQ's decline; anything outside this is a misread field.</summary>
    private static readonly DateTimeOffset Earliest = new(1996, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string Platform => PlatformId;

    public string DisplayName => "QIP / QIP Infium";

    public ImportDetection Detect(string path)
    {
        var files = HistoryFiles(path);

        if (files.Length == 0)
        {
            return ImportDetection.No;
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

        var owner = OwnerUin(path, files[0]);

        return new ImportDetection(
            ImportConfidence.Certain,
            AccountId: owner,
            AccountName: owner is null ? null : $"QIP {owner}",
            FileCount: files.Length,
            Note: "The QIP format was never published; this reader follows a community reverse "
                + "engineering of it and stops rather than guessing if a file does not match.");
    }

    public void Read(string path, IImportSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var files = HistoryFiles(path);

        if (files.Length == 0)
        {
            throw new InvalidDataException($"'{path}' contains no .qhf history files.");
        }

        var ownerUin = OwnerUin(path, files[0]) ?? "self";

        var owner = new NormalizedIdentity(
            PlatformId, ownerUin, Handle: null, $"QIP {ownerUin}", IsSynthetic: false);

        sink.OnOwner(owner);

        foreach (var file in files)
        {
            ReadHistory(file, owner, sink);
        }
    }

    private static string[] HistoryFiles(string path)
    {
        if (File.Exists(path))
        {
            return path.EndsWith(".qhf", StringComparison.OrdinalIgnoreCase) ? [path] : [];
        }

        return Directory.Exists(path)
            ? [.. Directory.EnumerateFiles(path, "*.qhf", SearchOption.AllDirectories)
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
    /// The account's own UIN, taken from the folder layout.
    /// </summary>
    /// <remarks>
    /// QIP stores histories under <c>&lt;profile&gt;\&lt;own UIN&gt;\History\&lt;contact&gt;.qhf</c>, and the
    /// file itself names only the *contact*, so the numeric folder above History is the only thing
    /// that says who "me" is.
    ///
    /// When the layout is not recognizable — a bare pile of .qhf files — "self" stands in. Each
    /// message keeps the direction its own block records, so the conversation still reads
    /// correctly; what is lost is the account's real identity, which means this owner will not
    /// merge with the same person arriving from any other import.
    /// </remarks>
    private static string? OwnerUin(string root, string firstFile)
    {
        // Bounded to a few levels: the UIN sits directly above History, and someone may point at
        // the History folder itself, at the profile above it, or at the QIP folder above that.
        // Only folder names are read, and never more than this far up.
        const int Levels = 4;

        var directory = new DirectoryInfo(Path.GetDirectoryName(firstFile)!);

        for (var i = 0; i < Levels && directory is not null; i++)
        {
            if (directory.Name.Length > 0 && directory.Name.All(char.IsAsciiDigit))
            {
                return directory.Name;
            }

            directory = directory.Parent;
        }

        return null;
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

        var contact = new NormalizedIdentity(
            PlatformId, uin, Handle: null,
            string.IsNullOrWhiteSpace(nickname) ? uin : nickname, IsSynthetic: false);

        // One file is one conversation, and QIP had no group chats worth the name.
        var thread = new NormalizedThread(uin, "dm", contact.DisplayName);
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
