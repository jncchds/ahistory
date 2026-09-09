using System.Buffers;
using System.Text.Json;

namespace Archive.Import.Telegram;

/// <summary>Receives the pieces of an export as the reader streams through it.</summary>
public interface ITelegramExportSink
{
    /// <summary>The export's personal_information block, which identifies the owner (§2).</summary>
    void OnPersonalInformation(JsonElement element);

    /// <summary>Called once per chat, before any of its messages.</summary>
    void OnChat(TelegramChatHeader chat);

    /// <summary>Called once per message, in export order.</summary>
    void OnMessage(TelegramChatHeader chat, JsonElement message);
}

/// <summary>
/// Streams a Telegram Desktop JSON export without materializing it.
/// </summary>
/// <remarks>
/// <para>
/// A real export runs to hundreds of megabytes and sometimes past a gigabyte, so the document is
/// never loaded whole. A <see cref="Utf8JsonReader"/> walks a fixed buffer that is refilled as it
/// is consumed; only one message is materialized at a time, as a <see cref="JsonElement"/>, which
/// is what lets the importer keep the raw JSON per message (§1) without keeping the file.
/// </para>
/// <para>
/// Both export shapes are handled: a full export (<c>chats.list</c>, plus <c>left_chats.list</c>)
/// and a single-chat export, whose chat fields sit at the root. left_chats is not skipped —
/// those are entire conversations with people you no longer share a chat with, and dropping them
/// would quietly lose whole relationships from the archive.
/// </para>
/// </remarks>
public static class TelegramExportReader
{
    private const int InitialBufferSize = 128 * 1024;

    /// <summary>Guards against a single pathological value consuming unbounded memory.</summary>
    private const int MaxBufferSize = 64 * 1024 * 1024;

    public static void Read(Stream stream, ITelegramExportSink sink)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sink);

        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        var context = new ParseContext(sink);

        try
        {
            var dataLength = 0;
            var isFinalBlock = false;
            var state = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            while (true)
            {
                if (!isFinalBlock)
                {
                    var read = stream.Read(buffer, dataLength, buffer.Length - dataLength);

                    if (read == 0)
                    {
                        isFinalBlock = true;
                    }

                    dataLength += read;
                }

                var consumed = ParseChunk(buffer.AsSpan(0, dataLength), isFinalBlock, ref state, context);

                if (consumed > 0 && consumed < dataLength)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, dataLength - consumed);
                }

                dataLength -= consumed;

                if (isFinalBlock)
                {
                    break;
                }

                // Nothing could be consumed and the buffer is full: one value is larger than the
                // window, so the window has to grow. Usually a message with an enormous text body.
                if (dataLength == buffer.Length)
                {
                    if (buffer.Length >= MaxBufferSize)
                    {
                        throw new InvalidDataException(
                            $"A single JSON value exceeds {MaxBufferSize / (1024 * 1024)} MB; the export is probably corrupt.");
                    }

                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, larger, 0, dataLength);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads as far as the buffer allows and reports how many bytes were fully handled.
    /// </summary>
    /// <remarks>
    /// The returned position and the returned reader state must describe the same point. They are
    /// captured together after each successfully handled token, so that a value which turns out
    /// to be incomplete leaves both pointing at the token before it rather than half past it.
    /// </remarks>
    private static int ParseChunk(
        ReadOnlySpan<byte> span,
        bool isFinalBlock,
        ref JsonReaderState state,
        ParseContext context)
    {
        var reader = new Utf8JsonReader(span, isFinalBlock, state);

        long safeConsumed = 0;
        var safeState = state;

        while (reader.Read())
        {
            if (!context.Handle(ref reader))
            {
                // A whole value was needed and is not yet in the buffer.
                break;
            }

            safeConsumed = reader.BytesConsumed;
            safeState = reader.CurrentState;
        }

        state = safeState;
        return (int)safeConsumed;
    }

    /// <summary>
    /// Tracks where in the document the reader is, and materializes only what must be whole.
    /// </summary>
    /// <remarks>
    /// Position is tracked with an explicit stack of the property names that opened each
    /// container, rather than by reasoning about numeric depth. Depth arithmetic is the kind of
    /// thing that works until an export nests one level differently and then silently attributes
    /// messages to the wrong chat.
    /// </remarks>
    private sealed class ParseContext(ITelegramExportSink sink)
    {
        private readonly List<string?> _path = [];
        private string? _pendingName;

        private string? _chatName;
        private string? _chatType;
        private string? _chatId;
        private TelegramChatHeader? _chat;

        /// <summary>Handles one token. Returns false when more buffered data is required.</summary>
        internal bool Handle(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    _pendingName = reader.GetString();
                    return true;

                case JsonTokenType.StartObject:
                    return HandleStartObject(ref reader);

                case JsonTokenType.StartArray:
                    if (InChatScope && _pendingName == "messages")
                    {
                        // The chat header is complete by the time its messages begin.
                        _chat = new TelegramChatHeader(_chatName, _chatType, _chatId, IsLeftChats);
                        sink.OnChat(_chat);
                    }

                    Push();
                    return true;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    Pop();
                    return true;

                default:
                    CaptureChatField(ref reader);
                    _pendingName = null;
                    return true;
            }
        }

        private bool HandleStartObject(ref Utf8JsonReader reader)
        {
            // A message: the one value that must be materialized whole, so the importer can keep
            // its raw JSON and read fields in any order.
            if (InMessagesArray)
            {
                if (!TryParseValue(ref reader, out var message))
                {
                    return false;
                }

                sink.OnMessage(
                    _chat ?? throw new InvalidDataException("Encountered a message outside any chat."),
                    message);

                return true;
            }

            // personal_information identifies the owner and is small (§2).
            if (_path.Count == 1 && _pendingName == "personal_information")
            {
                if (!TryParseValue(ref reader, out var personal))
                {
                    return false;
                }

                sink.OnPersonalInformation(personal);
                _pendingName = null;
                return true;
            }

            // Entering a chat object resets the header being accumulated.
            if (_pendingName is null && _path.Count == 3 && IsChatsList)
            {
                _chatName = _chatType = _chatId = null;
                _chat = null;
            }

            Push();
            return true;
        }

        /// <summary>
        /// Parses the value at the reader, or reports that the buffer does not hold all of it yet.
        /// </summary>
        /// <remarks>
        /// TrySkip is run against a copy first. It answers "is this whole value present?" without
        /// disturbing the real reader, so a value that straddles a buffer boundary can be retried
        /// after a refill instead of throwing.
        /// </remarks>
        private static bool TryParseValue(ref Utf8JsonReader reader, out JsonElement element)
        {
            var probe = reader;

            if (!probe.TrySkip())
            {
                element = default;
                return false;
            }

            element = JsonElement.ParseValue(ref reader);
            return true;
        }

        private void CaptureChatField(ref Utf8JsonReader reader)
        {
            if (!InChatScope || _pendingName is null)
            {
                return;
            }

            var value = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out var n) ? n.ToString() : null,
                _ => null,
            };

            switch (_pendingName)
            {
                case "name": _chatName = value; break;
                case "type": _chatType = value; break;
                case "id": _chatId = value; break;
            }
        }

        private void Push()
        {
            _path.Add(_pendingName);
            _pendingName = null;
        }

        private void Pop()
        {
            if (_path.Count > 0)
            {
                _path.RemoveAt(_path.Count - 1);
            }

            _pendingName = null;
        }

        /// <summary>True inside the array of messages belonging to a chat.</summary>
        private bool InMessagesArray => _path.Count > 0 && _path[^1] == "messages";

        private bool IsChatsList =>
            _path.Count >= 3 && _path[2] == "list" && _path[1] is "chats" or "left_chats";

        private bool IsLeftChats => _path.Count >= 2 && _path[1] == "left_chats";

        /// <summary>
        /// True where a chat's own fields live: inside a chats.list element, or at the root of a
        /// single-chat export.
        /// </summary>
        private bool InChatScope => _path.Count == 1 || (_path.Count == 4 && IsChatsList);
    }
}
