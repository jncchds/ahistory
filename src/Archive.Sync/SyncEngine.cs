using System.Diagnostics;
using Archive.Data;
using Archive.Import;
using Archive.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Sync;

/// <summary>How a run should behave. Everything here is policy, not code.</summary>
/// <param name="Optimize">
/// Whether to merge the search index when the run ends. True for a read that was asked for, false
/// for a live session, which ends whenever the app closes.
/// </param>
public sealed record SyncOptions(
    bool StoreRawJson = true,
    bool Optimize = true,
    int LiveBatchSize = 25,
    TimeSpan? LiveFlushInterval = null);

public sealed record SyncProgress(string Chat, int ChatsDone, int ChatsTotal, long MessagesSeen, long MessagesInserted);

public sealed record SyncResult(string SourceId, ImportStats Stats, int ChatsRead, int ChatsNotIncluded);

/// <summary>
/// Reads an account into a save, and keeps reading it while the app is open.
/// </summary>
/// <remarks>
/// <para>
/// Platform-neutral: everything it knows about a platform arrives through <see cref="IChatSource"/>
/// as <see cref="NormalizedMessage"/>, which is what keeps the schema, the dedupe rules and every
/// page downstream unaware that an account exists at all.
/// </para>
/// <para>
/// The shape of a page is the point. Fetching, downloading and hashing all happen with no
/// transaction open; then one short transaction writes the messages <em>and</em> the cursor that
/// describes them. SQLite has a single writer, and a transaction held across a network call stalls
/// every other writer in the app — the same rule P1 states for model calls. A cursor written ahead
/// of its page would skip that page for ever after a crash; written behind it, the worst case is
/// reading one page twice, which the uid makes free.
/// </para>
/// </remarks>
public sealed class SyncEngine(
    Database database,
    IMediaStore mediaStore,
    ILogger<SyncEngine>? logger = null)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMediaStore _mediaStore = mediaStore ?? throw new ArgumentNullException(nameof(mediaStore));
    private readonly ILogger _log = logger ?? NullLogger<SyncEngine>.Instance;

    /// <summary>The scope <c>sync_state</c> keeps a connection's own catch-up state under.</summary>
    private const string AccountScope = "*";

    /// <summary>
    /// Lists the account's chats and reads the ones the user included, from wherever each got to.
    /// </summary>
    public async Task<SyncResult> SyncAsync(
        IChatSource source,
        SyncOptions? options = null,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        options ??= new SyncOptions();

        var store = new SyncStore(_database);
        var account = await source.AccountAsync(cancellationToken).ConfigureAwait(false);
        var sourceId = ImportSourceResolver.AccountSourceId(source.Platform, account.AccountId);

        // The committer first, because it is what creates the source row every chat and cursor
        // hangs off: recording chats before it exists fails the foreign key.
        using var committer = NewCommitter(source, account, sourceId, options);

        var stopwatch = Stopwatch.StartNew();
        var done = 0;
        var notIncluded = 0;

        try
        {
            SeedOwner(committer, source.Platform, account);

            var chats = await source.ChatsAsync(cancellationToken).ConfigureAwait(false);
            store.RecordChats(sourceId, chats);

            var known = store.Chats(sourceId, source.Platform);
            var included = known.Where(c => c.IsIncluded).ToArray();

            notIncluded = known.Count - included.Length;

            _log.LogInformation(
                "Sync of {Platform} source {SourceId}: {Included} chat(s) included, {Undecided} undecided, {Ignored} ignored.",
                source.Platform, sourceId, included.Length,
                known.Count(c => c.IsUndecided), known.Count(c => c.Decision == "ignore"));

            foreach (var chat in included)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remote = chats.FirstOrDefault(r => r.ChatId == chat.ChatId)
                    ?? new RemoteChat(chat.ChatId, chat.PeerKind, chat.ThreadKind, chat.Title, chat.LastMessageUnix);

                await ReadChatAsync(source, store, committer, remote, sourceId, cancellationToken).ConfigureAwait(false);

                done++;

                progress?.Report(new SyncProgress(
                    remote.Title ?? remote.ChatId, done, included.Length,
                    committer.Stats.MessagesSeen, committer.Stats.MessagesInserted));
            }

            committer.Complete(options.Optimize);

            _log.LogInformation(
                "Sync of {SourceId} finished in {ElapsedMs} ms: {Stats}.",
                sourceId, stopwatch.ElapsedMilliseconds, committer.Stats);

            return new SyncResult(sourceId, committer.Stats, done, notIncluded);
        }
        catch (Exception ex)
        {
            // Whatever was committed stays — every page is its own transaction with its own cursor,
            // so the next run carries on rather than starting again.
            committer.Fail(ex.Message);

            _log.LogError(
                ex, "Sync of {SourceId} stopped after {Chats} chat(s) and {Seen} message(s).",
                sourceId, done, committer.Stats.MessagesSeen);

            throw;
        }
    }

    /// <summary>Reads one chat from its cursor to the end, a page at a time.</summary>
    private async Task ReadChatAsync(
        IChatSource source,
        SyncStore store,
        ImportCommitter committer,
        RemoteChat chat,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var cursor = store.Cursor(sourceId, chat.ChatId);
        var thread = new NormalizedThread(chat.ChatId, chat.ThreadKind, chat.Title);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // No transaction is open here, and that is the point: this call is a network round trip
            // and may sit in a flood wait for minutes.
            var page = await source
                .ReadAsync(chat, cursor, uid => !store.KnownUids([uid]).Contains(uid), cancellationToken)
                .ConfigureAwait(false);

            var stored = await StoreMediaAsync(page, cancellationToken).ConfigureAwait(false);

            Commit(committer, chat, thread, page, stored, cursor);

            cursor = page.NextCursor;

            if (page.IsComplete || cursor is null)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Puts a page's attachments in the media store, before any transaction is open.
    /// </summary>
    /// <remarks>
    /// Hashing a video is not something to do with the write lock held. A source hands over the
    /// bytes only for messages it was told the archive does not have, so this is empty on a re-read.
    /// </remarks>
    private async Task<Dictionary<(string Uid, int Ordinal), StoredMedia>> StoreMediaAsync(
        RemoteMessagePage page, CancellationToken cancellationToken)
    {
        var stored = new Dictionary<(string, int), StoredMedia>();

        foreach (var message in page.Messages)
        {
            for (var ordinal = 0; ordinal < message.Media.Count; ordinal++)
            {
                var media = message.Media[ordinal];

                if (media.Content is not { } content)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var bytes = new MemoryStream(content, writable: false);

                var result = await _mediaStore
                    .PutAsync(bytes, Path.GetExtension(media.OriginalFilename ?? media.ExportPath))
                    .ConfigureAwait(false);

                stored[(message.Uid, ordinal)] = new StoredMedia(result.Hash, result.Extension, result.ByteSize);
            }
        }

        return stored;
    }

    /// <summary>Writes one page and the cursor that describes it, in one transaction.</summary>
    private static void Commit(
        ImportCommitter committer,
        RemoteChat chat,
        NormalizedThread thread,
        RemoteMessagePage page,
        Dictionary<(string Uid, int Ordinal), StoredMedia> stored,
        string? previousCursor)
    {
        if (page.Messages.Count > 0)
        {
            committer.EnsureThread(thread.SourceThreadId, thread.Kind, thread.Title);

            EnsureParticipants(committer, chat, page.Messages[0].SentAtUnix);
        }

        foreach (var message in page.Messages)
        {
            committer.Add(message, () =>
            {
                var files = new List<StoredMedia?>(message.Media.Count);

                for (var ordinal = 0; ordinal < message.Media.Count; ordinal++)
                {
                    files.Add(stored.TryGetValue((message.Uid, ordinal), out var file) ? file : null);
                }

                return files;
            });
        }

        // Even when a page brought nothing new, the cursor moved — otherwise the next run starts
        // from the same place and reads the same page for ever.
        committer.SetSyncState(chat.ChatId, page.NextCursor ?? previousCursor ?? string.Empty);

        committer.Checkpoint();
    }

    /// <summary>
    /// Records who is in a direct conversation, whether or not they said anything.
    /// </summary>
    /// <remarks>
    /// D28: participants answer "who was in the room", and §4's per-person view finds someone's
    /// direct threads through this table — so a conversation you wrote into and got no reply from
    /// would otherwise never appear in it.
    /// </remarks>
    private static void EnsureParticipants(ImportCommitter committer, RemoteChat chat, long firstSeenUnix)
    {
        if (chat.ThreadKind is not ("dm" or "saved") || chat.Peer is null)
        {
            return;
        }

        committer.EnsureParticipants(chat.ChatId, [chat.Peer], firstSeenUnix);
    }

    private ImportCommitter NewCommitter(
        IChatSource source, RemoteAccount account, string sourceId, SyncOptions options) =>
        new(_database,
            source.Platform,
            sourceId,
            $"{account.DisplayName} ({source.Platform})",

            // Where it came from, in the shape source_path already has for a folder. There is no
            // folder, and writing one that does not exist would be worse than saying so.
            $"{source.Platform}:account:{account.AccountId}",

            // A fingerprint identifies a set of bytes on disk; an account has none. The cursors say
            // how far this run got, and they are per chat.
            "account",
            batchSize: 500,
            storeRawJson: options.StoreRawJson);

    private static void SeedOwner(ImportCommitter committer, string platform, RemoteAccount account)
    {
        // Stated by the connection rather than guessed at, so it is a seed and not an 'auto' link
        // (D25): the account we are signed in as is not an inference.
        committer.SeedOwner(new NormalizedIdentity(
            platform, account.AccountId, account.Handle, account.DisplayName, IsSynthetic: false));

        committer.Checkpoint();
    }

    /// <summary>
    /// Stays connected and writes what arrives, until cancelled.
    /// </summary>
    /// <remarks>
    /// Messages are written in small batches rather than one at a time: a busy group would
    /// otherwise open a transaction per message. The batch is flushed on a timer as well, so a
    /// single message that arrives at midnight is in the archive a second later rather than
    /// whenever the next twenty-four arrive.
    /// </remarks>
    public async Task FollowAsync(
        IChatSource source,
        SyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        options ??= new SyncOptions();

        var store = new SyncStore(_database);
        var account = await source.AccountAsync(cancellationToken).ConfigureAwait(false);
        var sourceId = ImportSourceResolver.AccountSourceId(source.Platform, account.AccountId);

        using var committer = NewCommitter(source, account, sourceId, options with { Optimize = false });

        SeedOwner(committer, source.Platform, account);

        var sink = new LiveSink(this, source, store, committer, sourceId, options);

        try
        {
            await using var flusher = new Timer(
                _ => sink.FlushSafely(),
                null,
                sink.Interval,
                sink.Interval);

            await source.FollowAsync(sink, store.Cursor(sourceId, AccountScope), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is how this ends: the app is closing, or the user switched it off.
        }
        finally
        {
            sink.FlushSafely();
            committer.Complete(optimize: false);

            _log.LogInformation("Live sync of {SourceId} stopped. {Stats}", sourceId, committer.Stats);
        }
    }

    /// <summary>
    /// Collects what a connection reports and writes it in batches.
    /// </summary>
    /// <remarks>
    /// One lock around the buffer and the committer both: SQLite's connection is not thread-safe
    /// for concurrent writes, and an update can arrive while the timer is flushing.
    /// </remarks>
    private sealed class LiveSink(
        SyncEngine engine,
        IChatSource source,
        SyncStore store,
        ImportCommitter committer,
        string sourceId,
        SyncOptions options) : ILiveEvents
    {
        private readonly Lock _lock = new();
        private readonly List<(RemoteChat Chat, NormalizedMessage Message)> _pending = [];
        private readonly HashSet<string> _included = [];
        private string? _state;
        private DateTimeOffset _includedRead = DateTimeOffset.MinValue;

        internal TimeSpan Interval => options.LiveFlushInterval ?? TimeSpan.FromSeconds(2);

        public bool IsIncluded(string chatId)
        {
            lock (_lock)
            {
                // Re-read now and then rather than once: a chat included in the window while the
                // connection is up should start arriving without a restart.
                if (DateTimeOffset.UtcNow - _includedRead > TimeSpan.FromSeconds(30))
                {
                    _included.Clear();

                    foreach (var id in store.IncludedChatIds(sourceId))
                    {
                        _included.Add(id);
                    }

                    _includedRead = DateTimeOffset.UtcNow;
                }

                return _included.Contains(chatId);
            }
        }

        public Task OnMessageAsync(RemoteChat chat, NormalizedMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(chat);

            if (!IsIncluded(chat.ChatId))
            {
                return Task.CompletedTask;
            }

            lock (_lock)
            {
                _pending.Add((chat, message));

                if (_pending.Count < options.LiveBatchSize)
                {
                    return Task.CompletedTask;
                }
            }

            Flush();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Marks a message deleted, keeping it (P2).
        /// </summary>
        /// <remarks>
        /// With no chat named, every included chat of the kinds that share a numbering is tried.
        /// Each is an exact uid lookup, and at most one can match: a uid is unique.
        /// </remarks>
        public Task OnDeletedAsync(string? chatId, string messageId, CancellationToken cancellationToken = default)
        {
            // Whatever is buffered goes in first. A message deleted moments after it was sent is
            // the ordinary case — a typo, a wrong chat — and marking one that has not been written
            // yet would silently do nothing, leaving it in the archive with no sign it is gone.
            Flush();

            lock (_lock)
            {
                if (chatId is not null)
                {
                    committer.MarkDeleted(source.Uid(chatId, messageId));
                }
                else
                {
                    foreach (var candidate in store.IncludedChatIds(sourceId, "user", "chat"))
                    {
                        if (committer.MarkDeleted(source.Uid(candidate, messageId)))
                        {
                            break;
                        }
                    }
                }

                committer.Checkpoint();
            }

            return Task.CompletedTask;
        }

        public Task OnStateAsync(string state, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _state = state;
            }

            return Task.CompletedTask;
        }

        internal void FlushSafely()
        {
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                // A timer callback that throws takes the process with it, and a live session that
                // dies on one bad message is worse than one that skips a flush and tries again.
                engine._log.LogError(ex, "Writing live messages failed.");
            }
        }

        private void Flush()
        {
            (RemoteChat Chat, NormalizedMessage Message)[] batch;
            string? state;

            lock (_lock)
            {
                batch = [.. _pending];
                _pending.Clear();
                state = _state;
                _state = null;

                if (batch.Length == 0 && state is null)
                {
                    return;
                }

                foreach (var group in batch.GroupBy(p => p.Chat.ChatId, StringComparer.Ordinal))
                {
                    var chat = group.First().Chat;

                    committer.EnsureThread(chat.ChatId, chat.ThreadKind, chat.Title);

                    var messages = group.Select(g => g.Message).ToArray();

                    EnsureParticipants(committer, chat, messages[0].SentAtUnix);

                    foreach (var message in messages)
                    {
                        // Media on a live message is downloaded by the source before it is handed
                        // over, so there is nothing to resolve here.
                        committer.Add(message, () => StoredFrom(message));
                    }
                }

                if (state is not null)
                {
                    committer.SetSyncState(AccountScope, state);
                }

                committer.Checkpoint();
            }
        }

        private static IReadOnlyList<StoredMedia?> StoredFrom(NormalizedMessage message) =>
            [.. message.Media.Select(_ => (StoredMedia?)null)];
    }
}
