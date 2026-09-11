using Archive.Import;

namespace Archive.Sync;

/// <summary>The account a connection is signed in as — the archive's owner on that platform.</summary>
public sealed record RemoteAccount(string AccountId, string DisplayName, string? Handle = null);

/// <summary>One conversation an account has.</summary>
/// <param name="ChatId">
/// The platform's own id for it, and the same id an export of this account would use: it becomes
/// <c>thread.source_thread_id</c>, so a chat read from the account and the same chat read from an
/// export are one thread.
/// </param>
/// <param name="PeerKind">
/// The platform's classification, kept as the platform states it (Telegram: user, chat, channel).
/// The thread kind flattens this, and a deletion needs the difference back.
/// </param>
/// <param name="Peer">
/// The person on the other side of a direct conversation, when there is one. Recorded as a
/// participant even if they never said anything — who was in the room is not who spoke (D28), and
/// the per-person view finds a direct thread through its participants.
/// </param>
public sealed record RemoteChat(
    string ChatId,
    string PeerKind,
    string ThreadKind,
    string? Title,
    long? LastMessageUnix = null,
    NormalizedIdentity? Peer = null);

/// <summary>A page of history, and where to carry on from.</summary>
/// <param name="NextCursor">
/// What to pass back to read the next page. Written to <c>sync_state</c> in the same transaction as
/// these messages, so a crash re-reads a page rather than skipping one.
/// </param>
/// <param name="IsComplete">True when there is nothing older left to read in this chat.</param>
public sealed record RemoteMessagePage(
    IReadOnlyList<NormalizedMessage> Messages,
    string? NextCursor,
    bool IsComplete);

/// <summary>Where a following connection sends what arrives.</summary>
public interface ILiveEvents
{
    /// <summary>Whether this chat is one the user included. Asked before any work is done for it.</summary>
    bool IsIncluded(string chatId);

    Task OnMessageAsync(RemoteChat chat, NormalizedMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// The platform deleted a message.
    /// </summary>
    /// <param name="chatId">
    /// Null when the platform does not say which chat it was in — Telegram numbers messages in
    /// private chats and basic groups per account, so a deletion there carries ids alone.
    /// </param>
    Task OnDeletedAsync(string? chatId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>The connection's own catch-up state, to be stored and handed back next time.</summary>
    Task OnStateAsync(string state, CancellationToken cancellationToken = default);
}

/// <summary>
/// An account the app can read history from, rather than a folder it can read an export from.
/// </summary>
/// <remarks>
/// <para>
/// Everything downstream of <see cref="NormalizedMessage"/> is untouched by this, exactly as it was
/// untouched by adding a platform reader (D20): dedupe, revisions, media, sources and the schema do
/// not know whether a message arrived from a file or from a socket.
/// </para>
/// <para>
/// It is a separate interface from <c>IPlatformImporter</c> rather than a bigger version of it,
/// because the two have nothing in common at their edges: there is no folder to detect, reading is
/// resumable rather than one pass, and a connection keeps arriving after the read is over.
/// </para>
/// <para>
/// <b>A source produces the uids its platform's export reader would produce.</b> That is the whole
/// requirement, and the one worth testing against a real export: a message read from the account
/// and the same message read from an export must be one row, or the archive holds it twice.
/// </para>
/// </remarks>
public interface IChatSource : IAsyncDisposable
{
    /// <summary>The same platform id the file reader uses, so both routes share identities and threads.</summary>
    string Platform { get; }

    Task<RemoteAccount> AccountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteChat>> ChatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of a chat's history.
    /// </summary>
    /// <param name="wantsMedia">
    /// Asked before an attachment is downloaded, with the message's uid. False for a message the
    /// archive already has — on a re-read of a long chat that is nearly all of them, and a download
    /// nobody needs is the most expensive thing this can do.
    /// </param>
    Task<RemoteMessagePage> ReadAsync(
        RemoteChat chat,
        string? cursor,
        Func<string, bool> wantsMedia,
        CancellationToken cancellationToken = default);

    /// <summary>The uid a message of this platform has, given its chat and its own id.</summary>
    string Uid(string chatId, string messageId);

    /// <summary>
    /// Stays connected, reporting what arrives, until cancelled.
    /// </summary>
    /// <param name="state">What the last session stored, for catching up on what was missed.</param>
    Task FollowAsync(ILiveEvents events, string? state, CancellationToken cancellationToken = default);
}
