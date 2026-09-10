using System.Buffers.Binary;
using Archive.Import.Qip;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// QIP's <c>.qhf</c> is a closed binary format with no specification from its authors.
/// </summary>
/// <remarks>
/// The files here are built by <see cref="QipHistoryBuilder"/> rather than committed as binaries,
/// which makes that builder the executable statement of the layout the reader assumes — a
/// committed blob would be a magic number nobody could check. It also means the strictness tests
/// can corrupt one field at a time and assert the reader notices.
/// </remarks>
public sealed class QipImporterTests
{
    private const string OwnUin = "12345678";
    private const string ContactUin = "87654321";
    private const string Nickname = "Марина";

    private static readonly DateTimeOffset Start = new(2008, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static RecordingSink Read(string file)
    {
        var sink = new RecordingSink();
        new QipImporter().Read(file, sink);

        return sink;
    }

    /// <summary>A QIP profile with one contact's history in it.</summary>
    private static string Sample(string name = "qip") => Exports.WriteQip(Fixtures.Temp(name), OwnUin, ContactUin);

    /// <summary>The only file in a sample, for the tests that corrupt one field of it.</summary>
    private static string OnlyFile(string folder) => System.IO.Directory.GetFiles(folder)[0];

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
        var file = OnlyFile(folder);

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
        var history = QipHistoryBuilder.HistoryFolder(Fixtures.Temp("qip-service"), OwnUin);

        QipHistoryBuilder.Write(
            history, ContactUin, Nickname,
            new QipMessage(1, "привет", Outgoing: false, Start, Type: 1),
            new QipMessage(2, "", Outgoing: false, Start.AddMinutes(1), Type: 5),
            new QipMessage(3, "", Outgoing: true, Start.AddMinutes(2), Type: 14),
            new QipMessage(4, "позже", Outgoing: false, Start.AddMinutes(3), Type: 13));

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
        var file = OnlyFile(folder);

        var bytes = File.ReadAllBytes(file);

        // Field 3 is three bytes in every file seen. Claim four.
        BinaryPrimitives.WriteInt16BigEndian(
            bytes.AsSpan(QipHistoryBuilder.HeaderLength(ContactUin, Nickname) + 0x18), 4);

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
            new QipImporter().Detect(Exports.WriteGroupAndDm("qip-not-telegram")).Confidence);
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
        Assert.Equal(OwnUin, sink.Owner!.SourceIdentityId);

        var outgoing = sink.Messages.Single(m => m.Message.Plaintext == "как дела");
        var incoming = sink.Messages.Single(m => m.Message.Plaintext == "привет");

        Assert.Equal(OwnUin, outgoing.Message.Sender!.SourceIdentityId);
        Assert.Equal(ContactUin, incoming.Message.Sender!.SourceIdentityId);
        Assert.Equal(Nickname, incoming.Message.Sender.DisplayName);
    }

    [Fact]
    public void One_file_is_one_conversation()
    {
        var thread = Assert.Single(Read(Sample()).Threads);

        Assert.Equal(ContactUin, thread.SourceThreadId);
        Assert.Equal("dm", thread.Kind);
        Assert.Equal(Nickname, thread.Title);
    }

    [Fact]
    public void Uids_are_stable_across_reads()
    {
        var folder = Sample();

        Assert.Equal(
            Read(folder).Messages.Select(m => m.Message.Uid),
            Read(folder).Messages.Select(m => m.Message.Uid));
    }

    // Who "me" is. A .qhf names only the contact, so the owner comes from the folder above
    // History, from the user, or from nowhere — and the reader has to be honest about which.

    /// <summary>
    /// A history file for your own UIN is Saved Messages, not a conversation with yourself.
    /// </summary>
    /// <remarks>
    /// This is the phantom self-chat, at its source. QIP writes a file under your own UIN for
    /// messages you sent yourself and for authorization traffic; read as an ordinary contact it
    /// became a 'dm' titled with your own nickname, which nothing downstream could tell from a
    /// real conversation. Telegram's Saved Messages have always been 'saved' (D6); so is this.
    /// </remarks>
    [Fact]
    public void A_history_file_for_your_own_uin_is_saved_messages()
    {
        var history = QipHistoryBuilder.HistoryFolder(Fixtures.Temp("qip-self"), OwnUin);

        QipHistoryBuilder.Write(
            history, OwnUin, "Я",
            new QipMessage(1, "не забыть про билеты", Outgoing: true, Start),
            new QipMessage(2, "", Outgoing: false, Start.AddMinutes(1), Type: 5));

        var sink = Read(history);
        var thread = Assert.Single(sink.Threads);

        Assert.Equal("saved", thread.Kind);
        Assert.Equal(QipImporter.SavedMessagesTitle, thread.Title);

        // One identity, and it is the owner's — not a second one wearing the file's nickname.
        Assert.All(sink.Messages, m => Assert.Equal(OwnUin, m.Message.Sender!.SourceIdentityId));
        Assert.Equal($"QIP {OwnUin}", sink.Owner!.DisplayName);
        Assert.All(sink.Messages, m => Assert.Equal(sink.Owner.DisplayName, m.Message.Sender!.DisplayName));

        // Whatever is in it keeps its own kind: an authorization event is still an event.
        Assert.Equal(["message", "service"], sink.Messages.Select(m => m.Message.Kind));
    }

    /// <summary>
    /// The owner is the numeric folder whose child is History, and nothing else.
    /// </summary>
    /// <remarks>
    /// The first version took the first all-digit folder name it met walking up four levels, so
    /// <c>backup/2009/*.qhf</c> made "2009" the archive's owner — and a contact's own folder could
    /// be picked just as easily, which attaches a real contact to you as the archive's subject
    /// (§1's poisoned knowledge base) with no confirmation anywhere.
    /// </remarks>
    [Fact]
    public void A_numeric_folder_that_is_not_a_qip_profile_is_not_the_owner()
    {
        var folder = Path.Combine(Fixtures.Temp("qip-year-folder"), "2009");

        QipHistoryBuilder.Write(
            folder, ContactUin, Nickname, new QipMessage(1, "привет", Outgoing: false, Start));

        var owner = Read(folder).Owner!;

        Assert.NotEqual("2009", owner.SourceIdentityId);
        Assert.True(owner.IsSynthetic);
    }

    /// <summary>
    /// A bare pile of .qhf files gets a placeholder owner, marked as the guess it is.
    /// </summary>
    /// <remarks>
    /// This is the common case — someone points at the files they kept, not at a QIP profile tree.
    /// Each message keeps the direction its own block records, so the conversation still reads
    /// correctly; what is lost is the account's identity. Marking that lets the merge UI show it
    /// and lets it be detached, neither of which was true when it was the fixed id "self".
    /// </remarks>
    [Fact]
    public void A_bare_pile_of_files_gets_a_placeholder_owner_marked_as_a_guess()
    {
        var folder = Fixtures.Temp("qip-bare-pile");

        QipHistoryBuilder.Write(
            folder, ContactUin, Nickname,
            new QipMessage(1, "привет", Outgoing: false, Start),
            new QipMessage(2, "как дела", Outgoing: true, Start.AddMinutes(1)));

        var sink = Read(folder);
        var owner = sink.Owner!;

        Assert.True(owner.IsSynthetic);
        Assert.StartsWith("folder:", owner.SourceIdentityId!, StringComparison.Ordinal);

        // The two sides are still two sides.
        Assert.Equal(
            [ContactUin, owner.SourceIdentityId],
            sink.Messages.Select(m => m.Message.Sender!.SourceIdentityId));
    }

    /// <summary>Told which UIN is yours, the owner stops being a guess and the self file lands right.</summary>
    [Fact]
    public void An_account_the_user_supplies_is_the_owner()
    {
        var folder = Fixtures.Temp("qip-stated");

        QipHistoryBuilder.Write(folder, ContactUin, Nickname,
            new QipMessage(1, "привет", Outgoing: false, Start));
        QipHistoryBuilder.Write(folder, OwnUin, "Я",
            new QipMessage(1, "заметка", Outgoing: true, Start));

        var sink = new RecordingSink();
        new QipImporter().Read(folder, sink, new ImportOptions(OwnerAccountId: OwnUin));

        Assert.Equal(OwnUin, sink.Owner!.SourceIdentityId);
        Assert.False(sink.Owner.IsSynthetic);

        Assert.Equal("saved", sink.Threads.Single(t => t.SourceThreadId == OwnUin).Kind);
        Assert.Equal("dm", sink.Threads.Single(t => t.SourceThreadId == ContactUin).Kind);
    }

    /// <summary>
    /// Two profiles under one folder is a question, not something to average.
    /// </summary>
    /// <remarks>
    /// D13: a save is one person's archive. Picking one of the two and attributing the other
    /// account's messages to it is exactly the silent-and-wrong outcome the readers refuse.
    /// </remarks>
    [Fact]
    public void Two_profiles_in_one_folder_are_refused_by_name()
    {
        var root = Fixtures.Temp("qip-two-profiles");

        QipHistoryBuilder.Write(
            QipHistoryBuilder.HistoryFolder(root, "11111111"), ContactUin, Nickname,
            new QipMessage(1, "привет", Outgoing: false, Start));

        QipHistoryBuilder.Write(
            QipHistoryBuilder.HistoryFolder(root, "22222222"), "33333333", "Дмитрий",
            new QipMessage(1, "здравствуй", Outgoing: false, Start));

        var error = Assert.Throws<InvalidDataException>(() => Read(root));

        Assert.Contains("11111111", error.Message, StringComparison.Ordinal);
        Assert.Contains("22222222", error.Message, StringComparison.Ordinal);
    }

    // .ahf is QIP's archived history. Its layout has never been confirmed against real files, and
    // writing a reader from a guessed layout is what D22 records going wrong — so it is named and
    // refused rather than parsed.

    [Fact]
    public void A_folder_of_archived_history_is_recognized_and_refused_by_name()
    {
        var folder = Fixtures.Temp("qip-ahf-only");
        QipHistoryBuilder.WriteArchivedPlaceholder(folder, ContactUin);

        var detection = new QipImporter().Detect(folder);

        Assert.Equal(ImportConfidence.Possible, detection.Confidence);
        Assert.Contains(".ahf", detection.Note!, StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains(".ahf", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A mixed folder imports what it can and says what it skipped.</summary>
    [Fact]
    public void Archived_files_beside_readable_ones_are_reported_rather_than_ignored()
    {
        var history = QipHistoryBuilder.HistoryFolder(Fixtures.Temp("qip-mixed"), OwnUin);

        QipHistoryBuilder.Write(
            history, ContactUin, Nickname, new QipMessage(1, "привет", Outgoing: false, Start));

        QipHistoryBuilder.WriteArchivedPlaceholder(history, "55555555");

        var detection = new QipImporter().Detect(history);

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Contains(".ahf", detection.Note!, StringComparison.Ordinal);

        Assert.Single(Read(history).Messages);
    }

    // The layout has gaps nobody has explained, so the reader validates every field it walks
    // past. These are the checks that turn a wrong assumption into a named byte offset instead of
    // an archive full of mojibake dated 1970.

    [Fact]
    public void A_file_that_lies_about_its_length_is_refused()
    {
        var folder = Sample("qip-bad-length");
        var file = OnlyFile(folder);

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
        var file = OnlyFile(folder);

        var bytes = File.ReadAllBytes(file);

        BinaryPrimitives.WriteInt16BigEndian(
            bytes.AsSpan(QipHistoryBuilder.HeaderLength(ContactUin, Nickname)), 99);

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
        var history = QipHistoryBuilder.HistoryFolder(Fixtures.Temp("qip-bad-date"), OwnUin);

        QipHistoryBuilder.Write(
            history, ContactUin, Nickname,
            new QipMessage(1, "привет", Outgoing: false, DateTimeOffset.UnixEpoch.AddSeconds(60)));

        var error = Assert.Throws<InvalidDataException>(() => Read(history));

        Assert.Contains("plausible", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_message_count_that_does_not_match_is_refused()
    {
        var folder = Sample("qip-bad-count");
        var file = OnlyFile(folder);

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
        var file = OnlyFile(folder);

        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length - 10)]);

        Assert.Throws<InvalidDataException>(() => Read(folder));
    }
}
