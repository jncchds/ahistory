using System.Globalization;
using System.Text.Json;
using Archive.Import;
using Archive.Import.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TL;
using WTelegram;

namespace Archive.Sync.Telegram;

/// <summary>
/// Telegram, read through its own client protocol with the user's own account.
/// </summary>
/// <remarks>
/// <para>
/// The transport half of a connection: sign in, list the chats, walk a chat's history, download an
/// attachment, and stay connected for what arrives next. Everything it produces is a
/// <see cref="NormalizedMessage"/> built by the archive's own Telegram reader, from JSON written by
/// <see cref="TelegramExportShape"/> — so a message read here and the same message read from an
/// export are one row.
/// </para>
/// <para>
/// <b>Nothing here is reached unless the user switched Telegram on and signed in.</b> Constructing
/// this class opens no connection; <see cref="ConnectAsync"/> does, and that is called from the one
/// place the user asked for it.
/// </para>
/// </remarks>
public sealed class TelegramSource : IChatSource
{
    private readonly TelegramSettings _settings;
    private readonly SecretFile _session;
    private readonly ILogger _log;
    private readonly Dictionary<string, InputPeer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<long, User> _users = [];
    private readonly Dictionary<long, ChatBase> _chats = [];

    private Client? _client;
    private SessionStream? _sessionStream;
    private string? _phone;

    public TelegramSource(
        TelegramSettings settings, SecretFile session, ILogger<TelegramSource>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _log = logger ?? NullLogger<TelegramSource>.Instance;
    }

    public string Platform => TelegramNormalizer.Platform;

    /// <summary>The signed-in account, once there is one.</summary>
    public User? Me => _client?.User;

    public bool IsSignedIn => _client?.User is not null;

    /// <summary>
    /// Opens the connection, and reports what signing in still needs.
    /// </summary>
    /// <returns>
    /// Null when the stored session is enough — the ordinary case after the first time. Otherwise
    /// the name of what to ask the user for: <c>verification_code</c>, <c>password</c>, and so on,
    /// each of which is handed back to <see cref="ContinueLoginAsync"/>.
    /// </returns>
    public async Task<string?> ConnectAsync(string? phoneNumber = null, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            throw new InvalidOperationException(
                "Telegram is switched off. Nothing is connected until it is switched on in settings.");
        }

        if (!_settings.HasApplication)
        {
            throw new InvalidOperationException(
                "Telegram needs an api_id and api_hash of your own, from my.telegram.org. "
                + "They identify the application, not you.");
        }

        _phone = phoneNumber;
        _sessionStream = new SessionStream(_session);
        _client = new Client(Config, _sessionStream);

        // Long flood waits are worth surfacing rather than sleeping through: a backfill that stops
        // and says "Telegram asked for 20 minutes" can be resumed, and one that silently hangs for
        // twenty minutes looks broken.
        _client.FloodRetryThreshold = 60;

        _log.LogInformation("Connecting to Telegram.");

        return await _client.Login(phoneNumber).ConfigureAwait(false);
    }

    /// <summary>Answers whatever sign-in asked for, and reports what it needs next.</summary>
    public async Task<string?> ContinueLoginAsync(string answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        var client = _client ?? throw new InvalidOperationException("Connect before signing in.");

        return await client.Login(answer).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs out on Telegram's side and forgets the session.
    /// </summary>
    /// <remarks>
    /// Deleting the file alone would leave the app authorized in the account's device list, which
    /// is not what "disconnect" means to anyone reading it.
    /// </remarks>
    public async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.Auth_LogOut().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RpcException or IOException or ObjectDisposedException)
            {
                // Offline, or the session was already invalid. The local half still has to go.
                _log.LogWarning(ex, "Telegram sign-out did not complete; removing the local session anyway.");
            }
        }

        await DisposeAsync().ConfigureAwait(false);

        _session.Delete();
    }

    private string? Config(string what) => what switch
    {
        "api_id" => _settings.ApiId?.ToString(CultureInfo.InvariantCulture),
        "api_hash" => _settings.ApiHash,
        "phone_number" => _phone,

        // Everything else — the code, the password — is answered by ContinueLoginAsync, so the
        // caller can ask the user rather than this class inventing a way to prompt.
        _ => null,
    };

    private Client Connected =>
        _client is { User: not null } client
            ? client
            : throw new InvalidOperationException("Not signed in to Telegram.");

    public async Task<RemoteAccount> AccountAsync(CancellationToken cancellationToken = default)
    {
        var me = Connected.User!;

        return await Task.FromResult(new RemoteAccount(
            me.id.ToString(CultureInfo.InvariantCulture),
            Name(me),
            me.MainUsername)).ConfigureAwait(false);
    }

    /// <summary>Every conversation the account has, as something the user can decide about.</summary>
    public async Task<IReadOnlyList<RemoteChat>> ChatsAsync(CancellationToken cancellationToken = default)
    {
        var dialogs = await Connected.Messages_GetAllDialogs().ConfigureAwait(false);

        foreach (var (id, user) in dialogs.users)
        {
            _users[id] = user;
        }

        foreach (var (id, chat) in dialogs.chats)
        {
            _chats[id] = chat;
        }

        var chats = new List<RemoteChat>();
        var lastMessage = dialogs.Messages.ToDictionary(m => (m.Peer?.ID ?? 0, m.ID), m => m.Date);

        foreach (var dialog in dialogs.Dialogs)
        {
            if (Chat(dialog.Peer) is not { } chat)
            {
                continue;
            }

            var when = lastMessage.TryGetValue((dialog.Peer.ID, dialog.TopMessage), out var date)
                ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : (long?)null;

            chats.Add(chat with { LastMessageUnix = when });

            if (dialogs.UserOrChat(dialog.Peer)?.ToInputPeer() is { } peer)
            {
                _peers[chat.ChatId] = peer;
            }
        }

        _log.LogInformation("Telegram lists {Count} chat(s).", chats.Count);

        return chats;
    }

    /// <summary>
    /// Describes one dialog, keeping the platform's own idea of what it is.
    /// </summary>
    /// <remarks>
    /// The chat id is the bare peer id, which is what a Telegram Desktop export writes — and what
    /// makes a chat read here and the same chat read from an export one thread rather than two.
    /// </remarks>
    private RemoteChat? Chat(Peer peer)
    {
        switch (peer)
        {
            case PeerUser user when _users.TryGetValue(user.user_id, out var person):
            {
                var id = user.user_id.ToString(CultureInfo.InvariantCulture);

                // A chat with yourself is Saved Messages, and is not a conversation with another
                // person (D6, D26).
                var kind = user.user_id == Connected.UserId ? "saved" : "dm";

                return new RemoteChat(
                    id, "user", kind, Name(person), null,
                    new NormalizedIdentity(Platform, id, person.MainUsername, Name(person), IsSynthetic: false));
            }

            case PeerChat basic when _chats.TryGetValue(basic.chat_id, out var group):
                return new RemoteChat(
                    basic.chat_id.ToString(CultureInfo.InvariantCulture), "chat", "group", group.Title);

            case PeerChannel channel when _chats.TryGetValue(channel.channel_id, out var broadcast):
                return new RemoteChat(
                    channel.channel_id.ToString(CultureInfo.InvariantCulture),
                    "channel",
                    broadcast.IsGroup ? "group" : "channel",
                    broadcast.Title);

            default:
                return null;
        }
    }

    /// <summary>
    /// Reads a page of a chat: older history first, then whatever arrived since the last run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two phases, both in the cursor. While <c>Done</c> is false the chat is walked backwards from
    /// the oldest message read so far, which is how Telegram pages history. Once there is nothing
    /// older, later runs ask only for messages newer than the newest one read.
    /// </para>
    /// <para>
    /// Catching up is itself paged, and that is the part worth being careful about: asking for
    /// "newer than N" returns the most recent hundred, not the oldest hundred, so a chat with two
    /// hundred new messages would leave a hole in the middle. The cursor therefore walks down from
    /// the top of the new stretch, and only moves <c>Newest</c> forward once the stretch is
    /// exhausted — so a crash halfway re-reads part of it rather than skipping it.
    /// </para>
    /// </remarks>
    public async Task<RemoteMessagePage> ReadAsync(
        RemoteChat chat,
        string? cursor,
        Func<string, bool> wantsMedia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(wantsMedia);

        var peer = await PeerFor(chat).ConfigureAwait(false);
        var state = ChatCursor.Parse(cursor);
        const int Limit = 100;

        var history = state.Done
            ? await Connected.Messages_GetHistory(peer, offset_id: state.CatchUpFrom, limit: Limit, min_id: state.Newest)
                .ConfigureAwait(false)
            : await Connected.Messages_GetHistory(peer, offset_id: state.Oldest, limit: Limit)
                .ConfigureAwait(false);

        Remember(history);

        var messages = history.Messages;

        if (messages.Length == 0)
        {
            // Backfill has reached the beginning; the next call starts catching up instead. When
            // catching up is what just came back empty, the chat is finished for now.
            var finished = state.Done
                ? state with { Newest = Math.Max(state.Newest, state.PendingNewest), CatchUpFrom = 0, PendingNewest = 0 }
                : state with { Done = true };

            return new RemoteMessagePage([], finished.ToString(), IsComplete: state.Done);
        }

        var lowest = messages.Min(m => m.ID);
        var highest = messages.Max(m => m.ID);

        var next = state.Done
            ? state with { CatchUpFrom = lowest, PendingNewest = Math.Max(state.PendingNewest, highest) }
            : state with { Oldest = lowest, Newest = Math.Max(state.Newest, highest) };

        var normalized = new List<NormalizedMessage>(messages.Length);

        foreach (var message in messages.OrderBy(m => m.ID))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ConvertAsync(chat, message, wantsMedia, cancellationToken).ConfigureAwait(false) is { } converted)
            {
                normalized.Add(converted);
            }
        }

        return new RemoteMessagePage(normalized, next.ToString(), IsComplete: false);
    }

    /// <summary>Turns one message off the wire into one the archive can store.</summary>
    private async Task<NormalizedMessage?> ConvertAsync(
        RemoteChat chat, MessageBase message, Func<string, bool> wantsMedia, CancellationToken cancellationToken)
    {
        var translated = TelegramExportShape.Translate(message, Connected.UserId, NameOf);

        if (translated is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(translated.Json.ToJsonString());

        var header = new TelegramChatHeader(chat.Title, ExportType(chat), chat.ChatId, IsLeft: false);
        var normalized = TelegramNormalizer.Normalize(header, document.RootElement);

        if (normalized.Media.Count == 0)
        {
            return normalized;
        }

        // The reader produced one media entry per field this translation wrote, in the same order,
        // so they line up by index. If that ever stops being true it is a bug here rather than
        // something to paper over: a photo attached to the wrong message is worse than no photo.
        if (normalized.Media.Count != translated.Attachments.Count)
        {
            throw new InvalidOperationException(
                $"Telegram message {normalized.Uid} translated to {translated.Attachments.Count} attachment(s) "
                + $"but read as {normalized.Media.Count}.");
        }

        var wanted = wantsMedia(normalized.Uid);
        var media = new List<NormalizedMedia>(normalized.Media.Count);

        for (var i = 0; i < normalized.Media.Count; i++)
        {
            media.Add(await ResolveAsync(normalized.Media[i], translated.Attachments[i], wanted, cancellationToken)
                .ConfigureAwait(false));
        }

        return normalized with { Media = media };
    }

    /// <summary>
    /// Downloads an attachment, or records why it was not downloaded.
    /// </summary>
    /// <remarks>
    /// A message with an attachment nobody fetched is a normal state, not a failure: the export
    /// reader has had exactly this since the beginning, for exports taken without media. What
    /// matters is that the reason is recorded rather than the message being dropped.
    /// </remarks>
    private async Task<NormalizedMedia> ResolveAsync(
        NormalizedMedia media, TelegramAttachment attachment, bool wanted, CancellationToken cancellationToken)
    {
        if (!wanted)
        {
            return media with { MissingReason = "already in the archive" };
        }

        if (Skip(attachment) is { } reason)
        {
            return media with { MissingReason = reason };
        }

        try
        {
            using var buffer = new MemoryStream();

            if (attachment.Photo is Photo photo)
            {
                await Connected.DownloadFileAsync(photo, buffer).ConfigureAwait(false);
            }
            else if (attachment.Document is Document document)
            {
                await Connected.DownloadFileAsync(document, buffer).ConfigureAwait(false);
            }
            else
            {
                return media with { MissingReason = "nothing to download" };
            }

            return media with { Content = buffer.ToArray() };
        }
        catch (Exception ex) when (ex is RpcException or IOException)
        {
            // One file that will not come down must not cost the message it was attached to.
            _log.LogWarning(ex, "Could not download a Telegram attachment; keeping the message without it.");

            return media with { MissingReason = "could not be downloaded" };
        }
    }

    /// <summary>Why this attachment is not being fetched, or null to fetch it.</summary>
    private string? Skip(TelegramAttachment attachment)
    {
        if (attachment.Size > _settings.MaxDownloadBytes && _settings.MaxDownloadBytes > 0)
        {
            return $"larger than the {_settings.MaxDownloadBytes / (1024 * 1024)} MB limit in settings";
        }

        return (attachment.IsPhoto, attachment.IsVoice, attachment.IsVideo) switch
        {
            (true, _, _) when !_settings.DownloadPhotos => "photos are not being downloaded",
            (_, true, _) when !_settings.DownloadVoice => "voice messages are not being downloaded",
            (_, _, true) when !_settings.DownloadVideo => "videos are not being downloaded",
            (false, false, false) when !_settings.DownloadFiles => "files are not being downloaded",
            _ => null,
        };
    }

    public string Uid(string chatId, string messageId) => TelegramNormalizer.Uid(chatId, messageId);

    /// <summary>
    /// Stays connected, reporting messages, edits and deletions as they happen.
    /// </summary>
    /// <remarks>
    /// The update manager is what makes this survive a gap: given the state from last time it asks
    /// Telegram for the difference rather than assuming nothing happened while the app was closed.
    /// </remarks>
    public async Task FollowAsync(ILiveEvents events, string? state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var manager = Connected.WithUpdateManager(
            update => OnUpdateAsync(update, events, cancellationToken),
            Restore(state));

        _log.LogInformation("Following Telegram for new messages.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

                // Stored as we go: a session that only saved its place on a clean exit would ask
                // for the difference from far too long ago after a crash.
                await events.OnStateAsync(Save(manager), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping is how following ends.
        }
        finally
        {
            await events.OnStateAsync(Save(manager), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task OnUpdateAsync(Update update, ILiveEvents events, CancellationToken cancellationToken)
    {
        switch (update)
        {
            // The channel forms derive from the chat forms, so they are matched first. Both mean
            // the same thing here: a message arrived, or one changed.
            case UpdateNewChannelMessage arrivedInChannel:
                await Arrived(arrivedInChannel.message).ConfigureAwait(false);
                break;

            case UpdateNewMessage arrived:
                await Arrived(arrived.message).ConfigureAwait(false);
                break;

            case UpdateEditChannelMessage editedInChannel:
                await Arrived(editedInChannel.message).ConfigureAwait(false);
                break;

            case UpdateEditMessage edited:
                await Arrived(edited.message).ConfigureAwait(false);
                break;

            // Channels number their messages per channel, so a deletion there says which one.
            case UpdateDeleteChannelMessages channel:
                foreach (var id in channel.messages)
                {
                    await events.OnDeletedAsync(
                        channel.channel_id.ToString(CultureInfo.InvariantCulture),
                        id.ToString(CultureInfo.InvariantCulture),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            // Private chats and basic groups share one numbering per account, so this carries ids
            // and no chat at all.
            case UpdateDeleteMessages deleted:
                foreach (var id in deleted.messages)
                {
                    await events.OnDeletedAsync(null, id.ToString(CultureInfo.InvariantCulture), cancellationToken)
                        .ConfigureAwait(false);
                }

                break;
        }

        async Task Arrived(MessageBase message)
        {
            if (message.Peer is not { } peer || Chat(peer) is not { } chat || !events.IsIncluded(chat.ChatId))
            {
                return;
            }

            if (await ConvertAsync(chat, message, _ => true, cancellationToken).ConfigureAwait(false) is { } converted)
            {
                await events.OnMessageAsync(chat, converted, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<long, UpdateManager.MBoxState> Restore(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<long, UpdateManager.MBoxState>>(state) ?? [];
        }
        catch (JsonException)
        {
            // Written by a version that stored something else. Starting fresh costs a longer
            // catch-up, which is the cheap failure here.
            return [];
        }
    }

    private static string Save(UpdateManager manager) =>
        JsonSerializer.Serialize(manager.State.ToDictionary(e => e.Key, e => e.Value));

    private async Task<InputPeer> PeerFor(RemoteChat chat)
    {
        if (_peers.TryGetValue(chat.ChatId, out var peer))
        {
            return peer;
        }

        // A chat decided about in an earlier session, before this one listed anything.
        await ChatsAsync().ConfigureAwait(false);

        return _peers.TryGetValue(chat.ChatId, out var found)
            ? found
            : throw new InvalidOperationException(
                $"Telegram does not offer chat {chat.ChatId} to this account any more. "
                + "Its history stays in the archive; nothing new can be read from it.");
    }

    /// <summary>
    /// Keeps the people and rooms a page mentioned, so names can be resolved later.
    /// </summary>
    /// <remarks>
    /// The two result shapes carry these dictionaries on the concrete type rather than on their
    /// shared base, and a slice is a kind of Messages_Messages, so matching that first covers it.
    /// </remarks>
    private void Remember(Messages_MessagesBase history)
    {
        var (users, chats) = history switch
        {
            Messages_Messages messages => (messages.users, messages.chats),
            Messages_ChannelMessages channel => (channel.users, channel.chats),
            _ => (null, null),
        };

        foreach (var (id, user) in users ?? [])
        {
            _users[id] = user;
        }

        foreach (var (id, chat) in chats ?? [])
        {
            _chats[id] = chat;
        }
    }

    /// <summary>What the export calls this kind of chat, for the reader that expects those words.</summary>
    private static string ExportType(RemoteChat chat) => chat.ThreadKind switch
    {
        "saved" => "saved_messages",
        "dm" => "personal_chat",
        "channel" => "private_channel",
        _ => "private_group",
    };

    private string? NameOf(Peer peer) => peer switch
    {
        PeerUser user => _users.TryGetValue(user.user_id, out var person) ? Name(person) : null,
        PeerChat chat => _chats.TryGetValue(chat.chat_id, out var group) ? group.Title : null,
        PeerChannel channel => _chats.TryGetValue(channel.channel_id, out var broadcast) ? broadcast.Title : null,
        _ => null,
    };

    private static string Name(User user)
    {
        var name = string.Join(' ', new[] { user.first_name, user.last_name }
            .Where(part => !string.IsNullOrWhiteSpace(part))).Trim();

        return name.Length > 0
            ? name
            : user.MainUsername ?? user.id.ToString(CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;

        _sessionStream?.Dispose();
        _sessionStream = null;

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// How far a chat has been read, in both directions.
    /// </summary>
    /// <param name="Oldest">The oldest message read; history is walked backwards from it.</param>
    /// <param name="Newest">The newest message read, once the walk backwards finished.</param>
    /// <param name="CatchUpFrom">Where the current catch-up has got down to, or 0 for its start.</param>
    /// <param name="PendingNewest">
    /// The newest message this catch-up has seen. Only becomes <paramref name="Newest"/> when the
    /// catch-up finishes, so an interrupted one re-reads rather than leaving a hole.
    /// </param>
    internal readonly record struct ChatCursor(int Oldest, int Newest, bool Done, int CatchUpFrom, int PendingNewest)
    {
        public static ChatCursor Parse(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                return default;
            }

            try
            {
                return JsonSerializer.Deserialize<ChatCursor>(cursor);
            }
            catch (JsonException)
            {
                // Unreadable: start over. Every message is keyed by uid, so re-reading a chat costs
                // time and writes nothing.
                return default;
            }
        }

        public override string ToString() => JsonSerializer.Serialize(this);
    }

    /// <summary>
    /// The session, kept in memory and written through to a protected file.
    /// </summary>
    /// <remarks>
    /// The client expects a seekable stream it can rewrite in place. Writing straight to the file
    /// would leave the session in plain text; this keeps it in memory and re-encrypts the whole
    /// thing on every write, which costs nothing at a few kilobytes.
    /// </remarks>
    private sealed class SessionStream : MemoryStream
    {
        private readonly SecretFile _file;

        internal SessionStream(SecretFile file)
        {
            _file = file;

            if (file.Read() is { } existing)
            {
                Write(existing, 0, existing.Length);
                Position = 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Persist();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            Persist();
        }

        public override void Flush()
        {
            base.Flush();
            Persist();
        }

        private void Persist() => _file.Write(ToArray());
    }
}
