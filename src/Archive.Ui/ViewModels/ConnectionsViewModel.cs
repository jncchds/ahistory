using System.Collections.ObjectModel;
using Archive.Data;
using Archive.Media;
using Archive.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>One chat of a connected account, and the decision about it.</summary>
public sealed partial class ChatChoice(SyncChat chat, Action<ChatChoice, string?> decide) : ObservableObject
{
    public string ChatId { get; } = chat.ChatId;

    public string Title { get; } = string.IsNullOrWhiteSpace(chat.Title) ? "(no title)" : chat.Title!;

    public string Kind { get; } = chat.ThreadKind;

    public long MessageCount { get; } = chat.MessageCount;

    /// <summary>When it was last active, or a dash: a chat nobody has written in for years is easy to skip.</summary>
    public string LastActive { get; } = chat.LastMessageUnix is { } unix
        ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("d MMM yyyy")
        : "—";

    [ObservableProperty]
    private string? _decision = chat.Decision;

    public bool IsIncluded => Decision == "include";

    public bool IsIgnored => Decision == "ignore";

    public bool IsUndecided => Decision is null;

    partial void OnDecisionChanged(string? value)
    {
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(IsIgnored));
        OnPropertyChanged(nameof(IsUndecided));
    }

    [RelayCommand]
    private void Include() => decide(this, "include");

    [RelayCommand]
    private void Ignore() => decide(this, "ignore");
}

/// <summary>
/// Accounts the archive reads directly, rather than through an export of them.
/// </summary>
/// <remarks>
/// <para>
/// This page is the switch, so it is always in the rail — the same exception the AI settings page
/// has, and for the same reason: a switch you cannot reach is not one. Everything behind it is off
/// until someone turns it on, and with it off no connection is made and no class of a platform's
/// is ever constructed.
/// </para>
/// <para>
/// Reading an account is not like reading a folder: it holds every channel someone follows as well
/// as everyone they have ever written to. So the chats are listed and decided rather than taken
/// wholesale, and nothing is read until it is included.
/// </para>
/// </remarks>
public sealed partial class ConnectionsViewModel : ViewModelBase
{
    private readonly SyncSettingsStore _settings;
    private readonly SyncStore _store;
    private readonly SyncEngine _engine;
    private readonly IAccountConnectionFactory? _connections;

    private IAccountConnection? _connection;
    private CancellationTokenSource? _following;

    public ConnectionsViewModel(
        SyncSettingsStore settings,
        Database database,
        IMediaStore mediaStore,
        IAccountConnectionFactory? connections = null,
        ILogger<ConnectionsViewModel>? logger = null)
        : base(logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = new SyncStore(database);
        _engine = new SyncEngine(database, mediaStore);
        _connections = connections;

        var current = settings.Load();

        _isEnabled = current.Telegram.Enabled;
        _apiId = current.Telegram.ApiId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _apiHash = current.Telegram.ApiHash;
        _keepReading = current.Telegram.Live;
    }

    public override string Title => "Accounts";

    public override string Glyph => "⇄";

    public override int Position => 25;

    /// <summary>Raised when reading an account changed the archive, so every other page reloads.</summary>
    public event Func<Task>? Imported;

    /// <summary>Whether the app may contact Telegram at all.</summary>
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string? _apiId;

    [ObservableProperty]
    private string? _apiHash;

    [ObservableProperty]
    private string? _phoneNumber;

    /// <summary>What signing in is waiting for, in the platform's own words. Null when it is not.</summary>
    [ObservableProperty]
    private string? _awaiting;

    [ObservableProperty]
    private string? _answer;

    [ObservableProperty]
    private string? _accountName;

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private string? _status;

    /// <summary>Whether to keep reading while the app is open. Nothing runs while it is closed.</summary>
    [ObservableProperty]
    private bool _keepReading;

    [ObservableProperty]
    private string? _chatFilter;

    public ObservableCollection<ChatChoice> Chats { get; } = [];

    /// <summary>The chats the filter box leaves showing.</summary>
    public IEnumerable<ChatChoice> VisibleChats =>
        string.IsNullOrWhiteSpace(ChatFilter)
            ? Chats
            : Chats.Where(c => c.Title.Contains(ChatFilter.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool HasChats => Chats.Count > 0;

    public int IncludedCount => Chats.Count(c => c.IsIncluded);

    public int UndecidedCount => Chats.Count(c => c.IsUndecided);

    /// <summary>Whether this build can connect to anything — false leaves the page a switch and an explanation.</summary>
    public bool CanConnect => _connections is not null;

    public bool HasApplication =>
        int.TryParse(ApiId, out var id) && id > 0 && !string.IsNullOrWhiteSpace(ApiHash);

    public bool CanSignIn => CanConnect && IsEnabled && HasApplication && !IsSignedIn && !IsWorking;

    public bool CanRead => IsSignedIn && !IsWorking && IncludedCount > 0;

    /// <summary>
    /// What the sign-in prompt is asking for, in words rather than the protocol's field name.
    /// </summary>
    public string AwaitingLabel => Awaiting switch
    {
        null => string.Empty,
        "verification_code" => "Telegram has sent you a code. Enter it:",
        "password" => "Your two-step verification password:",
        "phone_number" => "Your phone number, with country code:",
        _ => $"Telegram is asking for: {Awaiting!.Replace('_', ' ')}",
    };

    /// <summary>Whether what is being typed should be hidden — a password, never a code.</summary>
    public bool AwaitingIsSecret => Awaiting == "password";

    public string SessionNote =>
        SecretFile.IsEncryptedAtRest
            ? "Your session is kept outside the archive and encrypted for this Windows account."
            : "Your session is kept outside the archive, readable only by you. This platform has no key store the app uses.";

    partial void OnChatFilterChanged(string? value) => OnPropertyChanged(nameof(VisibleChats));

    partial void OnApiIdChanged(string? value) => OnPropertyChanged(nameof(CanSignIn));

    partial void OnApiHashChanged(string? value) => OnPropertyChanged(nameof(CanSignIn));

    /// <summary>
    /// Switching the connector on or off, which is the only thing on this page that decides whether
    /// anything is contacted at all.
    /// </summary>
    partial void OnIsEnabledChanged(bool value)
    {
        _settings.Update(s => s.Telegram.Enabled = value);

        if (!value)
        {
            // Off means off now, not at the next restart: stop following and drop the connection.
            StopFollowing();

            _connection?.Dispose();
            _connection = null;

            IsSignedIn = false;
            AccountName = null;
            Awaiting = null;
            Status = "Telegram is off. Nothing is contacted.";
        }

        OnPropertyChanged(nameof(CanSignIn));
    }

    partial void OnKeepReadingChanged(bool value)
    {
        _settings.Update(s => s.Telegram.Live = value);

        if (value)
        {
            StartFollowing();
        }
        else
        {
            StopFollowing();
        }
    }

    partial void OnIsWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(CanRead));
    }

    partial void OnIsSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(CanRead));
    }

    public override Task RefreshAsync()
    {
        LoadChats();

        return Task.CompletedTask;
    }

    /// <summary>Remembers the application the user registered, without switching anything on.</summary>
    [RelayCommand]
    private void SaveApplication()
    {
        var id = int.TryParse(ApiId, out var parsed) ? parsed : (int?)null;

        _settings.Update(s =>
        {
            s.Telegram.ApiId = id;
            s.Telegram.ApiHash = string.IsNullOrWhiteSpace(ApiHash) ? null : ApiHash!.Trim();
        });

        Status = id is null
            ? "An api_id is a number from my.telegram.org."
            : "Saved. Signing in is the next step.";

        OnPropertyChanged(nameof(HasApplication));
        OnPropertyChanged(nameof(CanSignIn));
    }

    /// <summary>Starts signing in, and reports whatever it asks for next.</summary>
    [RelayCommand]
    private async Task SignIn()
    {
        if (_connections is null || !IsEnabled)
        {
            return;
        }

        IsWorking = true;
        Error = null;

        try
        {
            _connection?.Dispose();
            _connection = _connections.Create(_settings.Load());

            Awaiting = await _connection.ConnectAsync(PhoneNumber).ConfigureAwait(true);

            await AfterSignInStep().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Signing in to an account failed.");
            Error = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Hands back the code, or the password, and reports what comes next.</summary>
    [RelayCommand]
    private async Task SubmitAnswer()
    {
        if (_connection is null || string.IsNullOrWhiteSpace(Answer))
        {
            return;
        }

        IsWorking = true;
        Error = null;

        try
        {
            Awaiting = await _connection.ContinueLoginAsync(Answer!.Trim()).ConfigureAwait(true);
            Answer = null;

            await AfterSignInStep().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Answering a sign-in question failed.");
            Error = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>
    /// After every sign-in step: either something else is needed, or we are in and the chats can be
    /// listed for the user to decide about.
    /// </summary>
    private async Task AfterSignInStep()
    {
        OnPropertyChanged(nameof(AwaitingLabel));
        OnPropertyChanged(nameof(AwaitingIsSecret));

        if (Awaiting is not null || _connection is null)
        {
            return;
        }

        IsSignedIn = _connection.IsSignedIn;
        AccountName = _connection.AccountName;
        Status = $"Signed in as {AccountName}. Listing chats…";

        // Listing writes no messages: it is what puts the decisions in front of the user.
        var result = await _engine.SyncAsync(_connection).ConfigureAwait(true);

        LoadChats();

        Status = UndecidedCount > 0
            ? $"{UndecidedCount} chat(s) to decide about. Nothing is read until you include it."
            : $"{result.ChatsRead} chat(s) read.";

        if (KeepReading)
        {
            StartFollowing();
        }
    }

    /// <summary>Reads the chats that were included, from wherever each one got to.</summary>
    [RelayCommand]
    private async Task ReadNow()
    {
        if (_connection is null)
        {
            return;
        }

        IsWorking = true;
        Error = null;
        Status = "Reading…";

        try
        {
            var progress = new Progress<SyncProgress>(p =>
                Status = $"{p.ChatsDone}/{p.ChatsTotal} chats — {p.MessagesInserted:N0} new message(s)");

            var result = await _engine.SyncAsync(_connection, progress: progress).ConfigureAwait(true);

            Status = $"{result.Stats.MessagesInserted:N0} new message(s) from {result.ChatsRead} chat(s).";

            LoadChats();

            if (result.Stats.MessagesInserted > 0 && Imported is { } handler)
            {
                await handler().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Reading an account failed.");
            Error = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        StopFollowing();

        if (_connection is not null)
        {
            try
            {
                await _connection.DisconnectAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Signing out on the platform's side can fail while offline; the local half still
                // goes, and saying so is better than leaving the page looking connected.
                Log.LogWarning(ex, "Signing out did not complete.");
                Error = ex.Message;
            }

            _connection = null;
        }

        IsSignedIn = false;
        AccountName = null;
        Awaiting = null;
        Status = "Signed out. What was already read stays in the archive.";
    }

    /// <summary>Includes every direct conversation that nobody has decided about yet.</summary>
    [RelayCommand]
    private void IncludeDirect() => DecideKind("dm", "include");

    [RelayCommand]
    private void IgnoreChannels() => DecideKind("channel", "ignore");

    private void DecideKind(string kind, string decision)
    {
        if (_store.SourceFor("telegram") is not { } sourceId)
        {
            return;
        }

        var changed = _store.DecideKind(sourceId, kind, decision);

        LoadChats();

        Status = $"{changed} {kind} chat(s) now {(decision == "include" ? "kept" : "skipped")}.";
    }

    /// <summary>Records one chat's decision, which is the user's and is never guessed.</summary>
    private void Decide(ChatChoice chat, string? decision)
    {
        if (_store.SourceFor("telegram") is not { } sourceId)
        {
            return;
        }

        _store.Decide(sourceId, chat.ChatId, decision);
        chat.Decision = decision;

        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(UndecidedCount));
        OnPropertyChanged(nameof(CanRead));
    }

    private void LoadChats()
    {
        Chats.Clear();

        if (_store.SourceFor("telegram") is { } sourceId)
        {
            foreach (var chat in _store.Chats(sourceId, "telegram"))
            {
                Chats.Add(new ChatChoice(chat, Decide));
            }
        }

        OnPropertyChanged(nameof(HasChats));
        OnPropertyChanged(nameof(VisibleChats));
        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(UndecidedCount));
        OnPropertyChanged(nameof(CanRead));
    }

    /// <summary>
    /// Keeps reading while the app is open.
    /// </summary>
    /// <remarks>
    /// Deliberately no daemon and no scheduled task: like the AI runner, this lives and dies with
    /// the window, and the catch-up when it next starts is what covers the time in between. That is
    /// a promise about the machine, not only about a feature.
    /// </remarks>
    private void StartFollowing()
    {
        if (_connection is null || _following is not null || !IsSignedIn)
        {
            return;
        }

        var stopping = new CancellationTokenSource();
        _following = stopping;

        _ = Task.Run(async () =>
        {
            try
            {
                await _engine.FollowAsync(_connection, cancellationToken: stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping is how following ends.
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Following an account stopped unexpectedly.");
            }
        });
    }

    private void StopFollowing()
    {
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
    }
}
