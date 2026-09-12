using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Archive.Import.Synthetic;

/// <summary>
/// Builds a Messages database with the shape <c>chat.db</c> has.
/// </summary>
/// <remarks>
/// <para>
/// Written in code and into a temporary folder, like every other export shape here: a file in the
/// source tree that looks like somebody's messages is one careless copy away from being somebody's
/// messages (D30).
/// </para>
/// <para>
/// Only the columns the reader uses are created. A real chat.db has upwards of sixty on
/// <c>message</c> alone, and copying them all would suggest this is a specification rather than
/// what it is — the shape as documented by the tools that read it (D22).
/// </para>
/// </remarks>
public sealed class IMessageDatabaseBuilder
{
    private readonly List<(long Id, string Guid, long Style, string? Name, string Identifier)> _chats = [];
    private readonly List<(long Id, string Handle)> _handles = [];
    private readonly List<(long Chat, long Handle)> _members = [];
    private readonly List<MessageRow> _messages = [];
    private readonly List<(long Message, string File, string? Mime, string? Name, long Sticker)> _attachments = [];

    private string _account = "p:+15550000000";
    private long _nextMessage = 1;

    private sealed record MessageRow(
        long Id,
        string Guid,
        long Chat,
        string? Text,
        byte[]? AttributedBody,
        long Date,
        long IsFromMe,
        long? Handle,
        string? Account,
        long ItemType,
        long AssociatedType,
        string? AssociatedGuid);

    public static IMessageDatabaseBuilder New() => new();

    /// <summary>The account the owner's own messages carry — how the reader works out whose Mac it was.</summary>
    public IMessageDatabaseBuilder Owner(string account)
    {
        _account = account;

        return this;
    }

    /// <summary>A conversation with one person.</summary>
    public IMessageDatabaseBuilder Chat(long id, string guid, string handle, string? displayName = null)
    {
        _chats.Add((id, guid, 45, displayName, handle));
        AddHandle(id, handle);

        return this;
    }

    /// <summary>A group, with everyone who is in it whether or not they ever speak (D28).</summary>
    public IMessageDatabaseBuilder Group(long id, string guid, string? displayName, params string[] handles)
    {
        _chats.Add((id, guid, 43, displayName, guid));

        foreach (var handle in handles)
        {
            AddHandle(id, handle);
        }

        return this;
    }

    private void AddHandle(long chat, string handle)
    {
        var existing = _handles.FirstOrDefault(h => h.Handle == handle);
        var id = existing.Handle is null ? _handles.Count + 1 : existing.Id;

        if (existing.Handle is null)
        {
            _handles.Add((id, handle));
        }

        _members.Add((chat, id));
    }

    /// <summary>A message with its text in the <c>text</c> column, as older Messages wrote it.</summary>
    public IMessageDatabaseBuilder Message(
        long chat, string guid, DateTimeOffset at, bool fromMe, string text, string? fromHandle = null) =>
        Add(chat, guid, at, fromMe, text, attributed: null, fromHandle, itemType: 0);

    /// <summary>
    /// A message whose words are in <c>attributedBody</c>, as Messages has written them since High
    /// Sierra.
    /// </summary>
    public IMessageDatabaseBuilder Attributed(
        long chat, string guid, DateTimeOffset at, bool fromMe, string text, string? fromHandle = null) =>
        Add(chat, guid, at, fromMe, text: null, TypedStream(text), fromHandle, itemType: 0);

    /// <summary>An event rather than something somebody said — a rename, someone leaving.</summary>
    public IMessageDatabaseBuilder Event(
        long chat, string guid, DateTimeOffset at, long itemType, string? fromHandle = null) =>
        Add(chat, guid, at, fromMe: false, text: null, attributed: null, fromHandle, itemType);

    /// <summary>A tapback: a row of its own, pointing at the message it is about.</summary>
    public IMessageDatabaseBuilder Tapback(
        long chat, string guid, DateTimeOffset at, bool fromMe, long type, string targetGuid, string? fromHandle = null)
    {
        Add(chat, guid, at, fromMe, text: null, attributed: null, fromHandle, itemType: 0);

        var last = _messages[^1];
        _messages[^1] = last with { AssociatedType = type, AssociatedGuid = "p:0/" + targetGuid };

        return this;
    }

    private IMessageDatabaseBuilder Add(
        long chat, string guid, DateTimeOffset at, bool fromMe, string? text, byte[]? attributed,
        string? fromHandle, long itemType)
    {
        var handle = fromHandle is null
            ? _members.FirstOrDefault(m => m.Chat == chat).Handle
            : _handles.First(h => h.Handle == fromHandle).Id;

        _messages.Add(new MessageRow(
            _nextMessage++,
            guid,
            chat,
            text,
            attributed,

            // Nanoseconds since 2001, which is what anything recent writes.
            (long)(at - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds * 1_000_000_000L,
            fromMe ? 1 : 0,
            fromMe ? null : handle,
            fromMe ? _account : null,
            itemType,
            0,
            null));

        return this;
    }

    /// <summary>An attachment on the message added last.</summary>
    public IMessageDatabaseBuilder Attachment(string fileName, string mime, bool sticker = false)
    {
        _attachments.Add((_messages[^1].Id, "~/Library/Messages/Attachments/ab/01/" + fileName, mime, fileName, sticker ? 1 : 0));

        return this;
    }

    /// <summary>
    /// Encodes text the way an archived <c>NSAttributedString</c> carries it.
    /// </summary>
    /// <remarks>
    /// The prefix is what the reader checks for, and this builder writes it to match the layout the
    /// reader was written against. That agreement proves the two were written from the same
    /// reading; only a real chat.db proves the reading (D22).
    /// </remarks>
    internal static byte[] TypedStream(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var blob = new List<byte>();

        blob.AddRange("streamtyped"u8.ToArray());
        blob.AddRange([0x81, 0xe8, 0x03, 0x84, 0x01, 0x40, 0x84, 0x84, 0x84]);
        blob.AddRange("NSString"u8.ToArray());
        blob.AddRange([0x01, 0x94, 0x84, 0x01, 0x2B]);

        if (bytes.Length < 0x80)
        {
            blob.Add((byte)bytes.Length);
        }
        else
        {
            blob.Add(0x81);
            blob.Add((byte)(bytes.Length & 0xFF));
            blob.Add((byte)((bytes.Length >> 8) & 0xFF));
        }

        blob.AddRange(bytes);

        return [.. blob];
    }

    /// <summary>Writes the database, and the attachment files it names, into a folder.</summary>
    public string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "chat.db");

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();

            Execute(connection, Schema);

            foreach (var (id, guid, style, name, identifier) in _chats)
            {
                Execute(connection,
                    "INSERT INTO chat (ROWID, guid, style, display_name, chat_identifier) VALUES ($id, $guid, $style, $name, $identifier);",
                    ("$id", id), ("$guid", guid), ("$style", style), ("$name", name), ("$identifier", identifier));
            }

            foreach (var (id, handle) in _handles)
            {
                Execute(connection, "INSERT INTO handle (ROWID, id) VALUES ($id, $handle);", ("$id", id), ("$handle", handle));
            }

            foreach (var (chat, handle) in _members.Distinct())
            {
                Execute(connection,
                    "INSERT INTO chat_handle_join (chat_id, handle_id) VALUES ($chat, $handle);",
                    ("$chat", chat), ("$handle", handle));
            }

            foreach (var message in _messages)
            {
                Execute(connection, """
                    INSERT INTO message (ROWID, guid, text, attributedBody, date, is_from_me, handle_id,
                                         account, service, item_type, associated_message_type,
                                         associated_message_guid, cache_has_attachments)
                    VALUES ($id, $guid, $text, $body, $date, $me, $handle, $account, 'iMessage',
                            $item, $associated, $target, 0);
                    """,
                    ("$id", message.Id), ("$guid", message.Guid), ("$text", message.Text),
                    ("$body", message.AttributedBody), ("$date", message.Date), ("$me", message.IsFromMe),
                    ("$handle", message.Handle), ("$account", message.Account), ("$item", message.ItemType),
                    ("$associated", message.AssociatedType), ("$target", message.AssociatedGuid));

                Execute(connection,
                    "INSERT INTO chat_message_join (chat_id, message_id) VALUES ($chat, $message);",
                    ("$chat", message.Chat), ("$message", message.Id));
            }

            var attachmentId = 1L;

            foreach (var (message, file, mime, name, sticker) in _attachments)
            {
                Execute(connection, """
                    INSERT INTO attachment (ROWID, filename, mime_type, transfer_name, total_bytes, is_sticker)
                    VALUES ($id, $file, $mime, $name, 16, $sticker);
                    """,
                    ("$id", attachmentId), ("$file", file), ("$mime", mime), ("$name", name), ("$sticker", sticker));

                Execute(connection,
                    "INSERT INTO message_attachment_join (message_id, attachment_id) VALUES ($message, $attachment);",
                    ("$message", message), ("$attachment", attachmentId));

                // The file itself, where the reader will look for it: below the folder holding the
                // database, which is the part of Messages' own path that survives being copied.
                SyntheticMedia.Write(
                    folder, file[file.IndexOf("Attachments/", StringComparison.Ordinal)..], (int)attachmentId);

                attachmentId++;
            }

            SqliteConnection.ClearPool(connection);
        }

        return folder;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>The columns this reader uses, and no more.</summary>
    private const string Schema = """
        CREATE TABLE handle (ROWID INTEGER PRIMARY KEY, id TEXT NOT NULL, service TEXT);

        CREATE TABLE chat (
            ROWID INTEGER PRIMARY KEY,
            guid TEXT NOT NULL,
            style INTEGER,
            display_name TEXT,
            chat_identifier TEXT,
            service_name TEXT);

        CREATE TABLE message (
            ROWID INTEGER PRIMARY KEY,
            guid TEXT NOT NULL,
            text TEXT,
            attributedBody BLOB,
            date INTEGER,
            is_from_me INTEGER,
            handle_id INTEGER,
            account TEXT,
            service TEXT,
            item_type INTEGER,
            associated_message_type INTEGER,
            associated_message_guid TEXT,
            cache_has_attachments INTEGER);

        CREATE TABLE chat_message_join (chat_id INTEGER, message_id INTEGER);
        CREATE TABLE chat_handle_join (chat_id INTEGER, handle_id INTEGER);

        CREATE TABLE attachment (
            ROWID INTEGER PRIMARY KEY,
            filename TEXT,
            mime_type TEXT,
            transfer_name TEXT,
            total_bytes INTEGER,
            is_sticker INTEGER);

        CREATE TABLE message_attachment_join (message_id INTEGER, attachment_id INTEGER);
        """;

    /// <summary>A timestamp in the unit older databases used, for the test that both are read.</summary>
    public static long Seconds(DateTimeOffset at) =>
        (long)(at - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{_chats.Count} chat(s), {_messages.Count} message(s)");
}
