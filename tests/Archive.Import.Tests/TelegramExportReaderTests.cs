using System.Text;
using System.Text.Json;
using Archive.Import.Telegram;

namespace Archive.Import.Tests;

public sealed class TelegramExportReaderTests
{
    [Fact]
    public void A_full_export_yields_the_owner_every_chat_and_every_message()
    {
        var sink = Read("group-and-dm");

        Assert.NotNull(sink.PersonalInformation);
        Assert.Equal("Kirill", sink.PersonalInformation!.Value.GetProperty("first_name").GetString());

        Assert.Equal(["Sam Ruiz", "Prague trip", "Old book club"], sink.Chats.Select(c => c.Name));
        Assert.Equal(5, sink.Messages.Count);
    }

    /// <summary>
    /// left_chats holds entire conversations with people you no longer share a chat with.
    /// Skipping that section would quietly lose whole relationships from the archive.
    /// </summary>
    [Fact]
    public void Chats_that_were_left_are_read_and_marked()
    {
        var sink = Read("group-and-dm");

        var left = Assert.Single(sink.Chats, c => c.IsLeft);

        Assert.Equal("Old book club", left.Name);
        Assert.Equal("300", left.Id);
        Assert.All(sink.Chats.Where(c => !c.IsLeft), c => Assert.False(c.IsLeft));
    }

    [Fact]
    public void Each_message_is_attributed_to_its_own_chat()
    {
        var sink = Read("group-and-dm");

        var byChat = sink.Messages
            .GroupBy(m => m.Chat.Id)
            .ToDictionary(g => g.Key!, g => g.Count());

        Assert.Equal(2, byChat["100"]);
        Assert.Equal(2, byChat["200"]);
        Assert.Equal(1, byChat["300"]);
    }

    [Fact]
    public void Chat_headers_carry_the_type_and_map_to_a_thread_kind()
    {
        var sink = Read("group-and-dm");

        Assert.Equal("dm", sink.Chats.Single(c => c.Id == "100").ThreadKind);
        Assert.Equal("group", sink.Chats.Single(c => c.Id == "200").ThreadKind);
    }

    /// <summary>Telegram Desktop can export a single conversation, with no chats wrapper.</summary>
    [Fact]
    public void A_single_chat_export_is_read_the_same_way()
    {
        var sink = Read("single-chat");

        Assert.Null(sink.PersonalInformation);

        var chat = Assert.Single(sink.Chats);
        Assert.Equal("Sam Ruiz", chat.Name);
        Assert.Equal("100", chat.Id);
        Assert.Single(sink.Messages);
    }

    [Fact]
    public void The_raw_json_of_each_message_is_preserved()
    {
        var sink = Read("single-chat");

        var raw = sink.Messages[0].Raw;

        Assert.Contains("exported from one chat only", raw, StringComparison.Ordinal);
        Assert.Contains("date_unixtime", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of the chunked reader. A one-byte window forces a refill at nearly every
    /// token and makes every value straddle a boundary, so if the resume position and the reader
    /// state can ever disagree, this is where it shows up.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(4096)]
    public void The_result_is_identical_however_the_stream_is_chunked(int chunkSize)
    {
        var expected = Read("group-and-dm");

        var bytes = File.ReadAllBytes(Fixtures.ResultJson("group-and-dm"));
        var sink = new RecordingSink();
        TelegramExportReader.Read(new ChunkedStream(bytes, chunkSize), sink);

        Assert.Equal(expected.Chats.Select(c => c.Id), sink.Chats.Select(c => c.Id));
        Assert.Equal(expected.Messages.Count, sink.Messages.Count);
        Assert.Equal(expected.Messages.Select(m => m.Raw), sink.Messages.Select(m => m.Raw));
    }

    [Fact]
    public void A_message_larger_than_the_initial_buffer_is_still_read()
    {
        // 400 KB of text in one message, against a 128 KB initial buffer: the reader has to grow
        // its window rather than fail. Real archives contain pasted documents this size.
        var big = new string('x', 400_000);
        var json = $$"""
            {
              "name": "Notes",
              "type": "saved_messages",
              "id": 1,
              "messages": [
                { "id": 1, "type": "message", "date_unixtime": "1554221523",
                  "from_id": "user1", "text": "{{big}}",
                  "text_entities": [ { "type": "plain", "text": "{{big}}" } ] }
              ]
            }
            """;

        var sink = new RecordingSink();
        TelegramExportReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), sink);

        var message = Assert.Single(sink.Messages);
        Assert.Equal(big, message.Element.GetProperty("text").GetString());
    }

    private static RecordingSink Read(string fixture)
    {
        var sink = new RecordingSink();
        using var stream = Fixtures.OpenResultJson(fixture);
        TelegramExportReader.Read(stream, sink);
        return sink;
    }

    private sealed record RecordedMessage(TelegramChatHeader Chat, JsonElement Element, string Raw);

    private sealed class RecordingSink : ITelegramExportSink
    {
        internal JsonElement? PersonalInformation { get; private set; }

        internal List<TelegramChatHeader> Chats { get; } = [];

        internal List<RecordedMessage> Messages { get; } = [];

        public void OnPersonalInformation(JsonElement element) => PersonalInformation = element.Clone();

        public void OnChat(TelegramChatHeader chat) => Chats.Add(chat);

        public void OnMessage(TelegramChatHeader chat, JsonElement message) =>
            Messages.Add(new RecordedMessage(chat, message.Clone(), message.GetRawText()));
    }

    /// <summary>A stream that hands out at most <c>chunk</c> bytes per read.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var toCopy = Math.Min(Math.Min(chunk, count), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
