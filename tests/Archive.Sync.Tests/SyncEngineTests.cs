using System.Security.Cryptography;
using System.Text;
using Archive.Import;

namespace Archive.Sync.Tests;

/// <summary>
/// Reading an account into a save: what is read, what is left out, where it carries on from, and
/// what a live connection does with a message and with a deletion.
/// </summary>
/// <remarks>
/// Everything here runs against a fake source. What a real one puts on the wire is its own
/// problem; what the engine does with what comes back is this one, and it is the half that decides
/// whether the archive ends up with the right rows.
/// </remarks>
public sealed class SyncEngineTests
{
    private static readonly RemoteAccount Me = new("777001", "Owner Synthetic", "owner");

    private static RemoteChat Dm(string id, string title, long? last = null) =>
        new(id, "user", "dm", title, last,
            new NormalizedIdentity("telegram", id, null, title, IsSynthetic: false));

    private static NormalizedMessage Message(string chatId, int id, string text, bool withPhoto = false)
    {
        var media = withPhoto
            ? new NormalizedMedia($"telegram:photo/{id}", "photo", $"{id}.jpg", "image/jpeg", null, null, 100, 100, null)
                { Content = Encoding.UTF8.GetBytes($"photo-{chatId}-{id}") }
            : null;

        return new NormalizedMessage
        {
            Uid = $"tg/{chatId}/{id}",
            SourceThreadId = chatId,
            Kind = "message",
            Sender = new NormalizedIdentity("telegram", chatId, null, "Sam", IsSynthetic: false),
            SentAtUtc = DateTimeOffset.FromUnixTimeSeconds(1554221520 + id).ToString("O"),
            SentAtUnix = 1554221520 + id,
            Plaintext = text,
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            Media = media is null ? [] : [media],
        };
    }

    [Fact]
    public async Task Only_the_chats_the_user_included_are_read()
    {
        using var save = new TempSave();

        var source = new FakeSource()
            .WithChat(Dm("5001", "Sam"), Message("5001", 1, "the harbour was freezing"))
            .WithChat(Dm("5002", "Alex"), Message("5002", 1, "see you thursday"))
            .WithChat(new RemoteChat("9000", "channel", "channel", "Some channel"), Message("9000", 1, "broadcast"));

        var engine = new SyncEngine(save.Database, save.MediaStore);

        // The first run lists the chats and reads nothing: nothing has been decided yet.
        var listing = await engine.SyncAsync(source);

        Assert.Equal(0, listing.ChatsRead);
        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM message;"));
        Assert.Equal(3, save.Scalar<long>("SELECT count(*) FROM sync_chat;"));

        var store = new SyncStore(save.Database);
        store.Decide(listing.SourceId, "5001", "include");
        store.Decide(listing.SourceId, "9000", "ignore");

        var second = await engine.SyncAsync(source);

        Assert.Equal(1, second.ChatsRead);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message;"));
        Assert.Equal("tg/5001/1", save.Scalar<string>("SELECT uid FROM message;"));
    }

    /// <summary>
    /// An account is not a folder: what it contributes is decided per chat, and a channel someone
    /// merely follows would otherwise bury the correspondence.
    /// </summary>
    [Fact]
    public async Task Chats_are_listed_with_what_they_are_so_they_can_be_decided()
    {
        using var save = new TempSave();

        var source = new FakeSource()
            .WithChat(Dm("5001", "Sam", last: 1554221521))
            .WithChat(new RemoteChat("9000", "channel", "channel", "Some channel", 1554221000));

        var result = await new SyncEngine(save.Database, save.MediaStore).SyncAsync(source);
        var chats = new SyncStore(save.Database).Chats(result.SourceId, "telegram");

        Assert.Equal(2, chats.Count);
        Assert.All(chats, c => Assert.True(c.IsUndecided));

        // Most recent first, so the ones worth deciding about are at the top.
        Assert.Equal("5001", chats[0].ChatId);
        Assert.Equal("dm", chats[0].ThreadKind);
        Assert.Equal("channel", chats[1].PeerKind);
    }

    [Fact]
    public async Task A_second_run_carries_on_from_where_the_first_stopped()
    {
        using var save = new TempSave();

        var source = new FakeSource()
            .WithChat(Dm("5001", "Sam"), Message("5001", 1, "one"), Message("5001", 2, "two"));

        var engine = new SyncEngine(save.Database, save.MediaStore);
        var first = await engine.SyncAsync(source);

        new SyncStore(save.Database).Decide(first.SourceId, "5001", "include");

        await engine.SyncAsync(source);

        // Two pages of one message each, so the cursor had to move for both to arrive.
        Assert.Equal(2, save.Scalar<long>("SELECT count(*) FROM message;"));

        source.Add("5001", Message("5001", 3, "three"));
        var third = await engine.SyncAsync(source);

        Assert.Equal(1, third.Stats.MessagesInserted);
        Assert.Equal(3, save.Scalar<long>("SELECT count(*) FROM message;"));

        // Resumed rather than re-read from the top: the last read asked for what follows message
        // two, and only the first page of the chat ever went out with no cursor at all.
        Assert.Equal("2", source.CursorsSeen[^1]);
        Assert.Single(source.CursorsSeen, c => c is null);
        Assert.Equal("3", new SyncStore(save.Database).Cursor(first.SourceId, "5001"));
    }

    /// <summary>
    /// The most expensive thing a sync can do is download a file the archive already has.
    /// </summary>
    [Fact]
    public async Task An_attachment_is_downloaded_once_and_not_again_on_a_re_read()
    {
        using var save = new TempSave();

        var source = new FakeSource().WithChat(Dm("5001", "Sam"), Message("5001", 1, "look", withPhoto: true));
        var engine = new SyncEngine(save.Database, save.MediaStore);

        var first = await engine.SyncAsync(source);
        new SyncStore(save.Database).Decide(first.SourceId, "5001", "include");

        await engine.SyncAsync(source);

        Assert.Equal([true], source.MediaAnswers);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM media;"));

        source.Reset();
        new SyncStore(save.Database).ForgetCursors(first.SourceId);

        await engine.SyncAsync(source);

        // Asked again, and told no: the message is already here.
        Assert.Equal([false], source.MediaAnswers);
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM media;"));
    }

    /// <summary>The account we are signed in as is stated, not inferred, so it seeds the owner (D25).</summary>
    [Fact]
    public async Task The_connected_account_becomes_the_archive_owner()
    {
        using var save = new TempSave();

        await new SyncEngine(save.Database, save.MediaStore).SyncAsync(new FakeSource());

        Assert.Equal("Owner Synthetic", save.Scalar<string>("SELECT display_name FROM person WHERE is_owner = 1;"));
        Assert.Equal("seed", save.Scalar<string>("SELECT confidence FROM identity_person WHERE identity_id = 'telegram:777001';"));
        Assert.Equal(0, save.Scalar<long>("SELECT is_synthetic FROM identity WHERE id = 'telegram:777001';"));
    }

    /// <summary>
    /// D28: a direct thread holds the person at the other end of it, whether or not they replied.
    /// The per-person view finds someone's direct threads through this table.
    /// </summary>
    [Fact]
    public async Task Both_people_are_in_a_direct_thread_even_when_only_one_of_them_spoke()
    {
        using var save = new TempSave();

        var mine = new NormalizedMessage
        {
            Uid = "tg/5001/1",
            SourceThreadId = "5001",
            Kind = "message",
            Sender = new NormalizedIdentity("telegram", "777001", "owner", "Owner Synthetic", IsSynthetic: false),
            SentAtUtc = DateTimeOffset.FromUnixTimeSeconds(1554221523).ToString("O"),
            SentAtUnix = 1554221523,
            Plaintext = "are you there?",
            ContentHash = "hash",
        };

        var source = new FakeSource().WithChat(Dm("5001", "Sam"), mine);
        var engine = new SyncEngine(save.Database, save.MediaStore);

        var first = await engine.SyncAsync(source);
        new SyncStore(save.Database).Decide(first.SourceId, "5001", "include");

        await engine.SyncAsync(source);

        Assert.Equal(
            1,
            save.Scalar<long>(
                "SELECT count(*) FROM thread_participant WHERE thread_id = 'telegram:5001' AND identity_id = 'telegram:5001';"));
    }

    [Fact]
    public async Task A_message_that_arrives_while_connected_is_stored()
    {
        using var save = new TempSave();

        var source = new FakeSource().WithChat(Dm("5001", "Sam"));
        var engine = new SyncEngine(save.Database, save.MediaStore);

        var first = await engine.SyncAsync(source);
        new SyncStore(save.Database).Decide(first.SourceId, "5001", "include");

        source.Live(events => events.OnMessageAsync(Dm("5001", "Sam"), Message("5001", 7, "just landed")));

        await engine.FollowAsync(source);

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message;"));
        Assert.Equal("just landed", save.Scalar<string>("SELECT plaintext FROM message;"));
    }

    /// <summary>
    /// P2 in the one place it is most tempting to break: the platform deleted it, so the archive
    /// says so and keeps every word.
    /// </summary>
    [Fact]
    public async Task A_deletion_marks_the_message_and_keeps_it()
    {
        using var save = new TempSave();

        var source = new FakeSource().WithChat(Dm("5001", "Sam"));
        var engine = new SyncEngine(save.Database, save.MediaStore);

        var first = await engine.SyncAsync(source);
        new SyncStore(save.Database).Decide(first.SourceId, "5001", "include");

        source.Live(async events =>
        {
            await events.OnMessageAsync(Dm("5001", "Sam"), Message("5001", 7, "that came out wrong"));
            await events.OnDeletedAsync(null, "7");
        });

        await engine.FollowAsync(source);

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message;"));
        Assert.Equal("that came out wrong", save.Scalar<string>("SELECT plaintext FROM message;"));
        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM message_deletion;"));
    }

    [Fact]
    public async Task A_message_in_a_chat_that_was_not_included_is_not_stored()
    {
        using var save = new TempSave();

        var source = new FakeSource().WithChat(Dm("5001", "Sam"));
        var engine = new SyncEngine(save.Database, save.MediaStore);

        await engine.SyncAsync(source);

        source.Live(events => events.OnMessageAsync(Dm("5001", "Sam"), Message("5001", 7, "just landed")));

        await engine.FollowAsync(source);

        Assert.Equal(0, save.Scalar<long>("SELECT count(*) FROM message;"));
    }

    /// <summary>
    /// What the connection needs to catch up on what it missed while the app was closed.
    /// </summary>
    [Fact]
    public async Task The_state_a_connection_reports_is_kept_for_the_next_session()
    {
        using var save = new TempSave();

        var source = new FakeSource();
        var engine = new SyncEngine(save.Database, save.MediaStore);
        var first = await engine.SyncAsync(source);

        source.Live(events => events.OnStateAsync("""{"pts":42}"""));

        await engine.FollowAsync(source);

        Assert.Equal("""{"pts":42}""", new SyncStore(save.Database).Cursor(first.SourceId, "*"));
    }

    /// <summary>
    /// A source whose pages are handed out one message at a time, so cursors are exercised.
    /// </summary>
    private sealed class FakeSource : IChatSource
    {
        private readonly List<RemoteChat> _chats = [];
        private readonly Dictionary<string, List<NormalizedMessage>> _messages = new(StringComparer.Ordinal);
        private Func<ILiveEvents, Task>? _live;

        public string Platform => "telegram";

        /// <summary>Every cursor the engine handed back, in order.</summary>
        internal List<string?> CursorsSeen { get; } = [];

        /// <summary>What the engine answered when asked whether an attachment was wanted.</summary>
        internal List<bool> MediaAnswers { get; } = [];

        internal FakeSource WithChat(RemoteChat chat, params NormalizedMessage[] messages)
        {
            _chats.Add(chat);
            _messages[chat.ChatId] = [.. messages];

            return this;
        }

        internal void Add(string chatId, NormalizedMessage message) => _messages[chatId].Add(message);

        internal void Live(Func<ILiveEvents, Task> live) => _live = live;

        internal void Reset()
        {
            CursorsSeen.Clear();
            MediaAnswers.Clear();
        }

        public Task<RemoteAccount> AccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Me);

        public Task<IReadOnlyList<RemoteChat>> ChatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteChat>>(_chats);

        public Task<RemoteMessagePage> ReadAsync(
            RemoteChat chat, string? cursor, Func<string, bool> wantsMedia, CancellationToken cancellationToken = default)
        {
            CursorsSeen.Add(cursor);

            var after = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var remaining = _messages[chat.ChatId]
                .Where(m => Number(m) > after)
                .OrderBy(Number)
                .ToArray();

            if (remaining.Length == 0)
            {
                return Task.FromResult(new RemoteMessagePage([], cursor, IsComplete: true));
            }

            // One message per page: small enough that a test can see the cursor move.
            var next = remaining[0];

            var carried = next.Media.Count == 0 ? next : WithMediaIfWanted(next, wantsMedia);

            return Task.FromResult(new RemoteMessagePage(
                [carried],
                Number(next).ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsComplete: remaining.Length == 1));
        }

        private NormalizedMessage WithMediaIfWanted(NormalizedMessage message, Func<string, bool> wantsMedia)
        {
            var wanted = wantsMedia(message.Uid);
            MediaAnswers.Add(wanted);

            return wanted
                ? message
                : message with
                {
                    Media = [.. message.Media.Select(m => m with { MissingReason = "not downloaded" } with { Content = null })],
                };
        }

        private static int Number(NormalizedMessage message) =>
            int.Parse(message.Uid[(message.Uid.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        public string Uid(string chatId, string messageId) => $"tg/{chatId}/{messageId}";

        public async Task FollowAsync(ILiveEvents events, string? state, CancellationToken cancellationToken = default)
        {
            if (_live is { } live)
            {
                await live(events);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
