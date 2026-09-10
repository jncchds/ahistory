using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Archive.Import.Qip;

namespace Archive.Import.Synthetic;

/// <summary>One line of a QIP history file.</summary>
/// <param name="Outgoing">True when the account owner sent it — the byte at <c>+0x1A</c>.</param>
/// <param name="Type">
/// The byte at <c>+0x1C</c>: 1 online, 13 offline, 5 and 14 authorization. Not the int16 at
/// <c>+0x06</c>, which is a field id and is 1 in every block of every file ever seen.
/// </param>
public sealed record QipMessage(int Id, string Text, bool Outgoing, DateTimeOffset At, byte Type = 1);

/// <summary>
/// Builds QIP / QIP Infium <c>.qhf</c> history files, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// This is the executable statement of the layout the reader assumes. A committed <c>.qhf</c>
/// would be a magic number nobody could check, and — being indistinguishable from a real one —
/// exactly the sort of file that gets quietly replaced with somebody's actual correspondence.
/// </para>
/// <para>
/// Every id and length marker is written out rather than left as a zeroed gap. They pin the
/// offsets of everything after them, and a builder that omitted them could not tell a reader that
/// reads the right field from one that reads a constant.
/// </para>
/// <para>
/// It pins the format; it cannot confirm it. The first version of this builder was written from
/// the same misreading of the format as the first version of the reader, and the two agreed with
/// each other while no real file could be opened at all (decisions.md D22).
/// </para>
/// </remarks>
public static class QipHistoryBuilder
{
    /// <summary>Bytes of the header before the contact's UIN.</summary>
    private const int UinLengthOffset = 0x2C;

    /// <summary>
    /// Writes one contact's history and returns the file path.
    /// </summary>
    /// <param name="folder">Where the file goes — QIP's own layout is <c>&lt;own UIN&gt;/History</c>.</param>
    /// <param name="uin">The contact's UIN, which is also the file name and the thread id.</param>
    /// <param name="nickname">The contact's nickname. A file names only the contact, never you.</param>
    public static string Write(string folder, string uin, string nickname, params QipMessage[] messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return WriteBlocks(folder, uin, nickname, [.. messages.Select(Block)]);
    }

    /// <summary>
    /// The same file, from blocks a caller has shaped itself.
    /// </summary>
    /// <remarks>
    /// For the tests that need a block the ordinary path would never produce — a wrong signature,
    /// a field length that has changed — so the reader's refusals can be exercised.
    /// </remarks>
    public static string WriteBlocks(string folder, string uin, string nickname, byte[][] blocks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(uin);
        ArgumentNullException.ThrowIfNull(blocks);

        Directory.CreateDirectory(folder);

        var uinBytes = Encoding.ASCII.GetBytes(uin);
        var nicknameBytes = Encoding.UTF8.GetBytes(nickname);

        var headerLength = HeaderLength(uin, nickname);
        var file = new byte[headerLength + blocks.Sum(b => b.Length)];

        file[0] = (byte)'Q';
        file[1] = (byte)'H';
        file[2] = (byte)'F';
        file[3] = 3;

        // Measures what follows the field, so it is 8 short of the file. Writing the whole length
        // here is what made the first version of these fixtures agree with a reader that could not
        // open a single real export.
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0x04), file.Length - 8);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0x22), blocks.Length);
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(UinLengthOffset), (short)uinBytes.Length);

        uinBytes.CopyTo(file.AsSpan(0x2E));

        var nicknameOffset = 0x2E + uinBytes.Length;
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(nicknameOffset), (short)nicknameBytes.Length);
        nicknameBytes.CopyTo(file.AsSpan(nicknameOffset + 2));

        var offset = headerLength;

        foreach (var block in blocks)
        {
            block.CopyTo(file.AsSpan(offset));
            offset += block.Length;
        }

        var path = Path.Combine(folder, uin + ".qhf");
        File.WriteAllBytes(path, file);

        return path;
    }

    /// <summary>Where the first message block starts, which is where a test corrupting one aims.</summary>
    public static int HeaderLength(string uin, string nickname)
    {
        ArgumentNullException.ThrowIfNull(uin);
        ArgumentNullException.ThrowIfNull(nickname);

        return 0x2E + Encoding.ASCII.GetByteCount(uin) + 2 + Encoding.UTF8.GetByteCount(nickname);
    }

    /// <summary>
    /// A QIP profile folder: <c>&lt;root&gt;/&lt;own UIN&gt;/History</c>.
    /// </summary>
    /// <remarks>
    /// The file itself names only the contact, so this folder is the only thing that says who "me"
    /// is. Tests that want the owner unknown write into a bare folder instead.
    /// </remarks>
    public static string HistoryFolder(string root, string ownUin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownUin);

        var folder = Path.Combine(root, ownUin, "History");
        Directory.CreateDirectory(folder);

        return folder;
    }

    /// <summary>One message block, byte for byte as QIP Infium writes them.</summary>
    public static byte[] Block(QipMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var encoded = QipImporter.Encode(message.Text);
        var block = new byte[0x23 + encoded.Length];

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x00), 1);

        // As in the header, the size counts what follows it — the six bytes it shares the block
        // with are not included.
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x02), block.Length - 6);

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x06), 1);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x08), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x0A), message.Id);

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x0E), 2);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x10), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x12), (int)message.At.ToUnixTimeSeconds());

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x16), 3);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x18), 3);
        block[0x1A] = message.Outgoing ? (byte)1 : (byte)0;
        block[0x1B] = 0;
        block[0x1C] = message.Type;

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x1D), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x1F), encoded.Length);
        encoded.CopyTo(block.AsSpan(0x23));

        return block;
    }

    /// <summary>
    /// A stand-in for a QIP archived-history file.
    /// </summary>
    /// <remarks>
    /// <c>.ahf</c> is the format QIP writes when history is archived. Nothing here parses one —
    /// its layout is unconfirmed, and guessing at it is the mistake D22 records. This writes a file
    /// with the extension and nothing readable in it, which is all that is needed to test that the
    /// importer says what it found instead of pretending the folder is unrecognizable.
    /// </remarks>
    public static string WriteArchivedPlaceholder(string folder, string uin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(uin);

        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, uin + ".ahf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("AHF" + uin.ToString(CultureInfo.InvariantCulture)));

        return path;
    }
}
