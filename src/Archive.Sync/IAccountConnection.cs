namespace Archive.Sync;

/// <summary>
/// A connection to an account: signing in, and reading it once signed in.
/// </summary>
/// <remarks>
/// <see cref="IChatSource"/> is what reading needs; this adds the part a person has to be present
/// for. It exists as an interface so a window can be tested without an account — sign-in is the
/// half of this feature with the most states and the least ability to be tried against the real
/// thing.
/// </remarks>
public interface IAccountConnection : IChatSource, IDisposable
{
    /// <summary>Whether a stored session is already signed in.</summary>
    bool IsSignedIn { get; }

    /// <summary>Who we are signed in as, for the window to show. Null until there is an answer.</summary>
    string? AccountName { get; }

    /// <summary>
    /// Opens the connection.
    /// </summary>
    /// <returns>
    /// Null when the stored session was enough. Otherwise what signing in needs next —
    /// <c>phone_number</c>, <c>verification_code</c>, <c>password</c> — to be asked of the user and
    /// handed to <see cref="ContinueLoginAsync"/>.
    /// </returns>
    Task<string?> ConnectAsync(string? phoneNumber = null, CancellationToken cancellationToken = default);

    /// <summary>Answers what sign-in asked for, and reports what it needs next, or null when done.</summary>
    Task<string?> ContinueLoginAsync(string answer);

    /// <summary>Signs out on the platform's side as well as here.</summary>
    Task DisconnectAsync();
}

/// <summary>Makes a connection from the settings, without the caller naming a platform's class.</summary>
/// <remarks>
/// The seam a test replaces, and the reason a page never constructs a Telegram client itself: with
/// the connector switched off, nothing here is called and nothing is contacted.
/// </remarks>
public interface IAccountConnectionFactory
{
    IAccountConnection Create(SyncSettings settings);
}
