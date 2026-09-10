using System.Buffers.Binary;
using System.Text;
using Archive.Import.Qip;

namespace Archive.Import.Tests;

/// <summary>
/// QIP's <c>.qhf</c> is a closed binary format with no specification from its authors.
/// </summary>
/// <remarks>
/// The fixtures here are built in code rather than committed as binaries, which makes this file
/// the executable statement of the layout the reader assumes — a committed blob would be a magic
/// number nobody could check. It also means the strictness tests can corrupt one field at a time
/// and assert the reader notices.
/// </remarks>
public sealed class QipImporterTests
{
    private static RecordingSink Read(string file)
    {
        var sink = new RecordingSink();
        new QipImporter().Read(file, sink);

        return sink;
    }

    /// <summary>A history file for one contact, laid out as the format documents.</summary>
    private static string WriteHistory(
        string folder, string uin, string nickname, params (string Text, bool Outgoing, DateTimeOffset At)[] messages)
    {
        var blocks = messages.Select((m, i) => MessageBlock(i + 1, m.Text, m.Outgoing, m.At)).ToArray();

        return WriteBlocks(folder, uin, nickname, blocks);
    }

    /// <summary>The same file, for tests that need to shape the blocks themselves.</summary>
    private static string WriteBlocks(string folder, string uin, string nickname, byte[][] blocks)
    {
        var uinBytes = Encoding.ASCII.GetBytes(uin);
        var nickBytes = Encoding.UTF8.GetBytes(nickname);

        var headerLength = 0x2E + uinBytes.Length + 2 + nickBytes.Length;
        var total = headerLength + blocks.Sum(b => b.Length);

        var file = new byte[total];

        file[0] = (byte)'Q';
        file[1] = (byte)'H';
        file[2] = (byte)'F';
        file[3] = 3;

        // Measures what follows the field, so it is 8 short of the file. Writing the whole length
        // here is what made these fixtures agree with a reader that could not open a real export.
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0x04), total - 8);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0x22), blocks.Length);
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(0x2C), (short)uinBytes.Length);

        uinBytes.CopyTo(file.AsSpan(0x2E));

        var nickOffset = 0x2E + uinBytes.Length;
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(nickOffset), (short)nickBytes.Length);
        nickBytes.CopyTo(file.AsSpan(nickOffset + 2));

        var offset = headerLength;

        foreach (var block in blocks)
        {
            block.CopyTo(file.AsSpan(offset));
            offset += block.Length;
        }

        var path = Path.Combine(folder, $"{uin}.qhf");
        File.WriteAllBytes(path, file);

        return path;
    }

    /// <summary>
    /// One message block, byte for byte as QIP Infium writes them.
    /// </summary>
    /// <remarks>
    /// Every id and length marker is written out rather than left as a zeroed gap. They are what
    /// pins the offsets of everything after them, and a fixture that omits them cannot tell a
    /// reader that reads the right field from one that reads a constant.
    /// </remarks>
    private static byte[] MessageBlock(
        int id, string text, bool outgoing, DateTimeOffset at, byte messageType = 1)
    {
        var encoded = QipImporter.Encode(text);
        var block = new byte[0x23 + encoded.Length];

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x00), 1);

        // As in the header, the size counts what follows it — the six bytes it shares the block
        // with are not included.
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x02), block.Length - 6);

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x06), 1);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x08), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x0A), id);

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x0E), 2);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x10), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x12), (int)at.ToUnixTimeSeconds());

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x16), 3);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x18), 3);
        block[0x1A] = outgoing ? (byte)1 : (byte)0;
        block[0x1B] = 0;
        block[0x1C] = messageType;

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x1D), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x1F), encoded.Length);
        encoded.CopyTo(block.AsSpan(0x23));

        return block;
    }

    private static string Sample(string name = "qip")
    {
        var folder = Path.Combine(Fixtures.Temp(name), "12345678", "History");
        System.IO.Directory.CreateDirectory(folder);

        WriteHistory(
            folder, "87654321", "Марина",
            ("привет", false, new DateTimeOffset(2008, 5, 1, 12, 0, 0, TimeSpan.Zero)),
            ("как дела", true, new DateTimeOffset(2008, 5, 1, 12, 1, 0, TimeSpan.Zero)),
            ("всё в порядке", false, new DateTimeOffset(2008, 5, 1, 12, 2, 0, TimeSpan.Zero)));

        return folder;
    }

    // These three pin the facts that only real QIP Infium files could establish. The reader was
    // first written to a reading of the format in which both size fields counted the whole of
    // what they introduced and the message type was the int16 at +0x06. Everything agreed —
    // because the fixtures were built from the same reading — and not one real file could be
    // opened. Fixtures cannot confirm a format; they can only keep a confirmed one from drifting.

    /// <summary>
    /// Both size fields measure what follows them, so a file sized the other way is refused.
    /// </summary>
    /// <remarks>
    /// This is the exact shape of the original bug: every real export is eight bytes longer than
    /// its own size field, and reading that field as the file's length rejected all of them as
    /// truncated.
    /// </remarks>
    [Fact]
    public void A_size_field_counting_the_whole_file_is_refused()
    {
        var folder = Sample("qip-whole-file-size");
        var file = System.IO.Directory.GetFiles(folder)[0];

        var bytes = File.ReadAllBytes(file);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0x04), bytes.Length);
        File.WriteAllBytes(file, bytes);

        Assert.Throws<InvalidDataException>(() => Read(folder));
    }

    /// <summary>
    /// The message type is the byte at +0x1C, not the int16 at +0x06.
    /// </summary>
    /// <remarks>
    /// +0x06 is a field id and is 1 in every block of every real file, so classifying on it made
    /// "service" unreachable — authorization requests were filed as ordinary chat. Nothing failed;
    /// the archive was just quietly wrong, which is why this asserts both directions.
    /// </remarks>
    [Fact]
    public void Authorization_messages_are_service_messages_and_ordinary_ones_are_not()
    {
        var folder = Fixtures.Temp("qip-service");
        var history = Path.Combine(folder, "12345678", "History");
        System.IO.Directory.CreateDirectory(history);

        var at = new DateTimeOffset(2008, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var blocks = new[]
        {
            MessageBlock(1, "привет", outgoing: false, at, messageType: 1),
            MessageBlock(2, "", outgoing: false, at.AddMinutes(1), messageType: 5),
            MessageBlock(3, "", outgoing: true, at.AddMinutes(2), messageType: 14),
            MessageBlock(4, "позже", outgoing: false, at.AddMinutes(3), messageType: 13),
        };

        WriteBlocks(history, "87654321", "Марина", blocks);

        var messages = Read(history).Messages.Select(m => m.Message).ToArray();

        Assert.Equal(
            ["message", "service", "service", "message"],
            messages.Select(m => m.Kind));

        Assert.Equal(
            [null, "authorization_request", "authorization_accepted", null],
            messages.Select(m => m.ServiceAction));
    }

    /// <summary>
    /// A block whose fields are not the lengths the fixed offsets assume is refused.
    /// </summary>
    /// <remarks>
    /// The blocks are really id/length/value triples. Reading them at fixed offsets is only safe
    /// while every length stays what it has always been, so the reader checks each marker — the
    /// alternative is assembling a message out of fields that have shifted underneath it.
    /// </remarks>
    [Fact]
    public void A_block_whose_field_markers_differ_is_refused()
    {
        var folder = Sample("qip-bad-marker");
        var file = System.IO.Directory.GetFiles(folder)[0];

        var bytes = File.ReadAllBytes(file);
        var headerLength = 0x2E + 8 + 2 + Encoding.UTF8.GetByteCount("Марина");

        // Field 3 is three bytes in every file seen. Claim four.
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(headerLength + 0x18), 4);
        File.WriteAllBytes(file, bytes);

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("field 3", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The obfuscation is its own inverse, which is presumably why QIP used it. Not encryption.
    /// </summary>
    [Fact]
    public void The_text_obfuscation_round_trips()
    {
        foreach (var text in new[] { "привет", "hello world", "", "ёлка 🎄" })
        {
            Assert.Equal(text, QipImporter.Decode(QipImporter.Encode(text)));
        }
    }

    [Fact]
    public void A_history_folder_is_recognized_by_the_file_signature()
    {
        var detection = new QipImporter().Detect(Sample());

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(1, detection.FileCount);
        Assert.Contains("reverse engineering", detection.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_telegram_export_is_not_mistaken_for_one()
    {
        Assert.Equal(
            ImportConfidence.None,
            new QipImporter().Detect(Fixtures.Directory("group-and-dm")).Confidence);
    }

    [Fact]
    public void Messages_are_read_with_their_text_and_times()
    {
        var sink = Read(Sample());

        Assert.Equal(3, sink.Messages.Count);
        Assert.Equal(["привет", "как дела", "всё в порядке"], sink.Messages.Select(m => m.Message.Plaintext));
        Assert.StartsWith("2008-05-01T12:00:00", sink.Messages[0].Message.SentAtUtc, StringComparison.Ordinal);
    }

    /// <summary>
    /// The file names only the contact. Without an owner every message would render as incoming
    /// and the conversation would read as a monologue.
    /// </summary>
    [Fact]
    public void The_owner_comes_from_the_folder_and_marks_outgoing_messages()
    {
        var sink = Read(Sample());

        Assert.NotNull(sink.Owner);
        Assert.Equal("12345678", sink.Owner!.SourceIdentityId);

        var outgoing = sink.Messages.Single(m => m.Message.Plaintext == "как дела");
        var incoming = sink.Messages.Single(m => m.Message.Plaintext == "привет");

        Assert.Equal("12345678", outgoing.Message.Sender!.SourceIdentityId);
        Assert.Equal("87654321", incoming.Message.Sender!.SourceIdentityId);
        Assert.Equal("Марина", incoming.Message.Sender.DisplayName);
    }

    [Fact]
    public void One_file_is_one_conversation()
    {
        var thread = Assert.Single(Read(Sample()).Threads);

        Assert.Equal("87654321", thread.SourceThreadId);
        Assert.Equal("dm", thread.Kind);
        Assert.Equal("Марина", thread.Title);
    }

    [Fact]
    public void Uids_are_stable_across_reads()
    {
        var folder = Sample();

        Assert.Equal(
            Read(folder).Messages.Select(m => m.Message.Uid),
            Read(folder).Messages.Select(m => m.Message.Uid));
    }

    // The layout has gaps nobody has explained, so the reader validates every field it walks
    // past. These are the checks that turn a wrong assumption into a named byte offset instead of
    // an archive full of mojibake dated 1970.

    [Fact]
    public void A_file_that_lies_about_its_length_is_refused()
    {
        var folder = Sample("qip-bad-length");
        var file = System.IO.Directory.GetFiles(folder)[0];

        var bytes = File.ReadAllBytes(file);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0x04), bytes.Length + 100);
        File.WriteAllBytes(file, bytes);

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("declares", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_with_the_wrong_signature_is_refused()
    {
        var folder = Sample("qip-bad-signature");
        var file = System.IO.Directory.GetFiles(folder)[0];

        var bytes = File.ReadAllBytes(file);
        var headerLength = 0x2E + 8 + 2 + Encoding.UTF8.GetByteCount("Марина");
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(headerLength), 99);
        File.WriteAllBytes(file, bytes);

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A misread timestamp field is the failure that would otherwise be invisible: every message
    /// silently dated 1970 and sorted into one heap.
    /// </summary>
    [Fact]
    public void An_implausible_timestamp_is_refused()
    {
        var folder = Fixtures.Temp("qip-bad-date");
        var history = Path.Combine(folder, "12345678", "History");
        System.IO.Directory.CreateDirectory(history);

        WriteHistory(history, "87654321", "Марина",
            ("привет", false, DateTimeOffset.UnixEpoch.AddSeconds(60)));

        var error = Assert.Throws<InvalidDataException>(() => Read(history));

        Assert.Contains("plausible", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_message_count_that_does_not_match_is_refused()
    {
        var folder = Sample("qip-bad-count");
        var file = System.IO.Directory.GetFiles(folder)[0];

        var bytes = File.ReadAllBytes(file);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0x22), 99);
        File.WriteAllBytes(file, bytes);

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("messages", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_truncated_file_is_refused()
    {
        var folder = Sample("qip-truncated");
        var file = System.IO.Directory.GetFiles(folder)[0];

        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length - 10)]);

        Assert.Throws<InvalidDataException>(() => Read(folder));
    }
}
