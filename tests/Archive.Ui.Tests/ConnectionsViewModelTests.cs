using Archive.Import;
using Archive.Sync;
using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

/// <summary>
/// The Accounts page, tested without an account: switching the connector on and off, signing in
/// one question at a time, deciding which chats belong in the archive, and reading them.
/// </summary>
public sealed class ConnectionsViewModelTests
{
    private static ConnectionsViewModel Page(TempSave save, FakeFactory? factory = null) =>
        new(save.Settings(), save.Database, save.MediaStore, factory);

    /// <summary>
    /// P1's shape, applied to a connector: off means no connection, not a disabled button that
    /// still reaches the network when pressed.
    /// </summary>
    [Fact]
    public async Task With_the_connector_off_nothing_is_contacted()
    {
        using var save = new TempSave();
        var factory = new FakeFactory();
        var page = Page(save, factory);

        Assert.False(page.IsEnabled);
        Assert.False(page.CanSignIn);

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Equal(0, factory.Created);
        Assert.False(page.IsSignedIn);
    }

    /// <summary>An account needs an application of its own before anything can be signed in to.</summary>
    [Fact]
    public void Signing_in_waits_for_an_application_of_your_own()
    {
        using var save = new TempSave();
        var page = Page(save, new FakeFactory());

        page.IsEnabled = true;

        Assert.False(page.HasApplication);
        Assert.False(page.CanSignIn);

        page.ApiId = "12345";
        page.ApiHash = "abc123";
        page.SaveApplicationCommand.Execute(null);

        Assert.True(page.HasApplication);
        Assert.True(page.CanSignIn);
    }

    [Fact]
    public async Task Signing_in_asks_for_the_code_then_lists_the_chats()
    {
        using var save = new TempSave();
        var connection = new FakeConnection("verification_code");
        var page = SignedOn(save, connection);

        await page.SignInCommand.ExecuteAsync(null);

        // One question, in words rather than the protocol's field name.
        Assert.Equal("verification_code", page.Awaiting);
        Assert.Contains("code", page.AwaitingLabel, StringComparison.OrdinalIgnoreCase);
        Assert.False(page.AwaitingIsSecret);
        Assert.False(page.IsSignedIn);

        page.Answer = "11111";
        await page.SubmitAnswerCommand.ExecuteAsync(null);

        Assert.Null(page.Awaiting);
        Assert.True(page.IsSignedIn);
        Assert.Equal("Owner Synthetic", page.AccountName);

        // Listed, and none of them read: every one is waiting to be decided about.
        Assert.Equal(2, page.Chats.Count);
        Assert.Equal(2, page.UndecidedCount);
        Assert.Equal(0, save.Queries.Summary().Messages);
    }

    /// <summary>A two-step password is hidden; a code read off another screen is not.</summary>
    [Fact]
    public async Task A_password_is_hidden_and_a_code_is_not()
    {
        using var save = new TempSave();
        var connection = new FakeConnection("verification_code", "password");
        var page = SignedOn(save, connection);

        await page.SignInCommand.ExecuteAsync(null);

        Assert.False(page.AwaitingIsSecret);

        page.Answer = "11111";
        await page.SubmitAnswerCommand.ExecuteAsync(null);

        Assert.Equal("password", page.Awaiting);
        Assert.True(page.AwaitingIsSecret);
    }

    [Fact]
    public async Task A_chat_contributes_nothing_until_it_is_kept()
    {
        using var save = new TempSave();
        var page = SignedOn(save, new FakeConnection());

        await page.SignInCommand.ExecuteAsync(null);

        Assert.False(page.CanRead);

        var sam = page.Chats.Single(c => c.Title == "Sam Ruiz");
        sam.IncludeCommand.Execute(null);

        Assert.True(sam.IsIncluded);
        Assert.Equal(1, page.IncludedCount);
        Assert.True(page.CanRead);

        await page.ReadNowCommand.ExecuteAsync(null);

        Assert.Null(page.Error);
        Assert.Equal(1, save.Queries.Summary().Messages);
        Assert.Contains("1 new message", page.Status!, StringComparison.Ordinal);
    }

    /// <summary>Every other page counts something out of the archive, so all of them are stale.</summary>
    [Fact]
    public async Task Reading_an_account_tells_the_rest_of_the_app()
    {
        using var save = new TempSave();
        var page = SignedOn(save, new FakeConnection());

        var notified = 0;
        page.Imported += () => { notified++; return Task.CompletedTask; };

        await page.SignInCommand.ExecuteAsync(null);

        page.Chats.Single(c => c.Title == "Sam Ruiz").IncludeCommand.Execute(null);
        await page.ReadNowCommand.ExecuteAsync(null);

        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task Channels_can_be_skipped_in_one_go()
    {
        using var save = new TempSave();
        var page = SignedOn(save, new FakeConnection());

        await page.SignInCommand.ExecuteAsync(null);

        page.IgnoreChannelsCommand.Execute(null);

        Assert.True(page.Chats.Single(c => c.Kind == "channel").IsIgnored);
        Assert.True(page.Chats.Single(c => c.Kind == "dm").IsUndecided);

        page.IncludeDirectCommand.Execute(null);

        Assert.True(page.Chats.Single(c => c.Kind == "dm").IsIncluded);
    }

    /// <summary>
    /// Switching it off takes effect now, not at the next restart — and what was already read
    /// stays, because the archive keeps what it has been given.
    /// </summary>
    [Fact]
    public async Task Switching_it_off_disconnects_and_keeps_what_was_read()
    {
        using var save = new TempSave();
        var page = SignedOn(save, new FakeConnection());

        await page.SignInCommand.ExecuteAsync(null);

        page.Chats.Single(c => c.Kind == "dm").IncludeCommand.Execute(null);
        await page.ReadNowCommand.ExecuteAsync(null);

        page.IsEnabled = false;

        Assert.False(page.IsSignedIn);
        Assert.Contains("Nothing is contacted", page.Status!, StringComparison.Ordinal);
        Assert.Equal(1, save.Queries.Summary().Messages);
    }

    [Fact]
    public async Task Signing_out_says_the_archive_keeps_what_it_read()
    {
        using var save = new TempSave();
        var connection = new FakeConnection();
        var page = SignedOn(save, connection);

        await page.SignInCommand.ExecuteAsync(null);
        await page.DisconnectCommand.ExecuteAsync(null);

        Assert.True(connection.SignedOut);
        Assert.False(page.IsSignedIn);
        Assert.Contains("stays in the archive", page.Status!, StringComparison.Ordinal);
    }

    /// <summary>A page with an application saved and the connector on, ready to sign in.</summary>
    private static ConnectionsViewModel SignedOn(TempSave save, FakeConnection connection)
    {
        var page = Page(save, new FakeFactory(connection));

        page.IsEnabled = true;
        page.ApiId = "12345";
        page.ApiHash = "abc123";
        page.SaveApplicationCommand.Execute(null);

        return page;
    }

    private sealed class FakeFactory(FakeConnection? connection = null) : IAccountConnectionFactory
    {
        internal int Created { get; private set; }

        public IAccountConnection Create(SyncSettings settings)
        {
            Created++;

            return connection ?? new FakeConnection();
        }
    }

    /// <summary>An account with one conversation and one channel, and a sign-in that asks questions.</summary>
    private sealed class FakeConnection(params string[] asks) : IAccountConnection
    {
        private readonly Queue<string> _asks = new(asks);

        internal bool SignedOut { get; private set; }

        public string Platform => "telegram";

        public bool IsSignedIn { get; private set; }

        public string? AccountName => IsSignedIn ? "Owner Synthetic" : null;

        public Task<string?> ConnectAsync(string? phoneNumber = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Next());

        public Task<string?> ContinueLoginAsync(string answer) => Task.FromResult(Next());

        private string? Next()
        {
            if (_asks.Count > 0)
            {
                return _asks.Dequeue();
            }

            IsSignedIn = true;
            return null;
        }

        public Task DisconnectAsync()
        {
            SignedOut = true;
            IsSignedIn = false;

            return Task.CompletedTask;
        }

        public Task<RemoteAccount> AccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteAccount("777001", "Owner Synthetic", "owner"));

        public Task<IReadOnlyList<RemoteChat>> ChatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteChat>>(
            [
                new RemoteChat("5001", "user", "dm", "Sam Ruiz", 1554221523,
                    new NormalizedIdentity("telegram", "5001", null, "Sam Ruiz", IsSynthetic: false)),
                new RemoteChat("9000", "channel", "channel", "Some channel", 1554220000),
            ]);

        public Task<RemoteMessagePage> ReadAsync(
            RemoteChat chat, string? cursor, Func<string, bool> wantsMedia, CancellationToken cancellationToken = default)
        {
            if (cursor is not null)
            {
                return Task.FromResult(new RemoteMessagePage([], cursor, IsComplete: true));
            }

            return Task.FromResult(new RemoteMessagePage(
                [
                    new NormalizedMessage
                    {
                        Uid = $"tg/{chat.ChatId}/1",
                        SourceThreadId = chat.ChatId,
                        Kind = "message",
                        Sender = new NormalizedIdentity("telegram", chat.ChatId, null, chat.Title!, IsSynthetic: false),
                        SentAtUtc = DateTimeOffset.FromUnixTimeSeconds(1554221523).ToString("O"),
                        SentAtUnix = 1554221523,
                        Plaintext = "the harbour was freezing",
                        ContentHash = "hash",
                    },
                ],
                "1",
                IsComplete: true));
        }

        public string Uid(string chatId, string messageId) => $"tg/{chatId}/{messageId}";

        public Task FollowAsync(ILiveEvents events, string? state, CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
