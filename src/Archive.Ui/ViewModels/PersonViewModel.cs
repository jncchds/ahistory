using System.Collections.ObjectModel;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>
/// One person, one continuous conversation (§4).
/// </summary>
/// <remarks>
/// Everything they said to you and you to them, plus what they said in groups, in one time
/// ordered stream — assembled by query, because group messages are stored once in their real
/// thread and never copied per participant.
/// </remarks>
public sealed partial class PersonViewModel(
    ArchiveQueries queries, PersonConversation conversation, ILogger<PersonViewModel>? logger = null)
    : ViewModelBase(logger)
{
    private const int PageSize = 100;

    /// <summary>
    /// How long a gap has to be before it is drawn.
    /// </summary>
    /// <remarks>
    /// Fixed for now, and it should not stay that way: a month of silence is enormous between
    /// daily correspondents and meaningless with someone you speak to twice a year. The threshold
    /// eventually has to be relative to that pair's own cadence (plan §5).
    /// </remarks>
    private static readonly TimeSpan SilenceThreshold = TimeSpan.FromDays(30);

    public override string Title => "Conversations";

    public override string Glyph => "☰";

    /// <summary>
    /// Raised when the stream has been rebuilt from the newest end.
    /// </summary>
    /// <remarks>
    /// The view scrolls to the bottom on this. A chat opens on what was said last, and a ten-year
    /// archive that opens on a page from 2014 looks like it failed to load the rest.
    /// </remarks>
    public event EventHandler? StreamReloaded;

    /// <summary>Raised with a message the view should scroll to and mark.</summary>
    public event EventHandler<long>? MessageRevealed;

    public ObservableCollection<PersonRow> People { get; } = [];

    /// <summary>
    /// Oldest first, so the conversation reads downward the way a chat does.
    /// </summary>
    /// <remarks>
    /// Pages are fetched newest-first, because that is what keyset paging from the present
    /// backwards gives you, and then inserted at the front. Reading order and fetch order are
    /// opposite on purpose: fetching newest-first is what makes opening a ten-year conversation
    /// instant, and reading oldest-first is what makes it a conversation rather than a log.
    /// </remarks>
    public ObservableCollection<ConversationItem> Items { get; } = [];

    [ObservableProperty]
    private PersonRow? _selectedPerson;

    [ObservableProperty]
    private string? _personFilter;

    /// <summary>True while older messages exist above what is loaded.</summary>
    [ObservableProperty]
    private bool _hasMore;

    /// <summary>
    /// True while newer messages exist below what is loaded.
    /// </summary>
    /// <remarks>
    /// Only ever true after arriving in the middle of a conversation. Opening one normally starts
    /// at the newest end, where there is nothing below by definition.
    /// </remarks>
    [ObservableProperty]
    private bool _hasNewer;

    /// <summary>
    /// Where the cursor for the next older page sits, and the next newer one.
    /// </summary>
    /// <remarks>
    /// Paging runs from both ends because the stream can be entered from the middle. Reaching a
    /// search hit from 2014 by paging backwards from today would be thousands of queries.
    /// </remarks>
    private long? _olderUnix;
    private long? _olderId;
    private long? _newerUnix;
    private long? _newerId;

    private long? _oldestLoadedUnix;
    private long? _newestLoadedUnix;

    /// <summary>
    /// One load at a time.
    /// </summary>
    /// <remarks>
    /// The view loads on scroll, and a scroll gesture raises many events. Without this, one flick
    /// at the top of the stream starts a dozen overlapping page queries whose results interleave.
    /// </remarks>
    private bool _isLoadingPage;

    /// <summary>Set while a reveal chooses the person itself, so the choice does not reload.</summary>
    private bool _suppressReload;

    private Task _pendingLoad = Task.CompletedTask;
    private Task _pendingRefresh = Task.CompletedTask;

    public override Task RefreshAsync() => _pendingRefresh = RefreshCoreAsync();

    private async Task RefreshCoreAsync()
    {
        await RunAsync(async () =>
        {
            var filter = PersonFilter;
            var people = await Task.Run(() => queries.People(filter)).ConfigureAwait(true);
            var previous = SelectedPerson?.Id;

            People.Clear();

            foreach (var person in people)
            {
                People.Add(person);
            }

            // Stay with whoever was being read. An import finishing must not move you.
            SelectedPerson = People.FirstOrDefault(p => p.Id == previous)
                ?? People.FirstOrDefault(p => !p.IsOwner)
                ?? People.FirstOrDefault();
        }).ConfigureAwait(true);

        await _pendingLoad.ConfigureAwait(true);
    }

    partial void OnSelectedPersonChanged(PersonRow? value)
    {
        if (!_suppressReload)
        {
            _pendingLoad = LoadFirstPageAsync();
        }
    }

    partial void OnPersonFilterChanged(string? value) => _ = RefreshAsync();

    /// <summary>
    /// Opens the conversation this message belongs to, positioned on it.
    /// </summary>
    /// <remarks>
    /// What "open in conversation" from a search result does. The message is loaded as the newest
    /// end of a backwards page and then read on from forwards, so a hit ten years deep costs the
    /// same two queries as opening the conversation normally does.
    /// </remarks>
    public async Task RevealAsync(string personId, long messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        // Navigating to this page starts a refresh of its own. Letting it finish first is what
        // stops it from clearing the stream underneath the message just revealed.
        await _pendingRefresh.ConfigureAwait(true);
        await _pendingLoad.ConfigureAwait(true);

        if (People.Count == 0)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        var person = People.FirstOrDefault(p => p.Id == personId);

        if (person is null)
        {
            // A person the filter has hidden is still somewhere to go, so the filter gives way.
            PersonFilter = null;
            await _pendingRefresh.ConfigureAwait(true);

            person = People.FirstOrDefault(p => p.Id == personId);
        }

        if (person is null)
        {
            Error = "That message is no longer in the archive.";
            return;
        }

        _suppressReload = true;
        SelectedPerson = person;
        _suppressReload = false;

        var loaded = await RunAsync(async () =>
        {
            var anchor = await Task.Run(() => conversation.Locate(messageId)).ConfigureAwait(true);

            if (anchor is null)
            {
                throw new InvalidOperationException("That message is no longer in the archive.");
            }

            ResetStream();

            // Inclusive: the message being revealed is the newest row of its own page, not the
            // first one off the end of it.
            await AppendOlderPageAsync(person.Id, anchor.SentAtUnix, anchor.Id, inclusive: true)
                .ConfigureAwait(true);

            await AppendNewerPageAsync(person.Id).ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (!loaded)
        {
            return;
        }

        foreach (var item in Items.OfType<MessageItem>())
        {
            item.IsRevealed = item.Row.Id == messageId;
        }

        MessageRevealed?.Invoke(this, messageId);
    }

    private Task LoadFirstPageAsync() => _pendingLoad = RunAsync(async () =>
    {
        ResetStream();

        if (SelectedPerson is not null)
        {
            await AppendOlderPageAsync(SelectedPerson.Id, null, null, inclusive: false)
                .ConfigureAwait(true);
        }

        StreamReloaded?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Loads the page above what is showing. Called as the view reaches the top.</summary>
    [RelayCommand]
    private Task LoadMore()
    {
        if (_isLoadingPage || !HasMore || SelectedPerson is null)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => AppendOlderPageAsync(SelectedPerson.Id, _olderUnix, _olderId, inclusive: false));
    }

    /// <summary>Loads the page below what is showing. Called as the view reaches the bottom.</summary>
    [RelayCommand]
    private Task LoadNewer()
    {
        if (_isLoadingPage || !HasNewer || SelectedPerson is null)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => AppendNewerPageAsync(SelectedPerson.Id));
    }

    private void ResetStream()
    {
        Items.Clear();
        _olderUnix = null;
        _olderId = null;
        _newerUnix = null;
        _newerId = null;
        _oldestLoadedUnix = null;
        _newestLoadedUnix = null;
        HasMore = false;
        HasNewer = false;
    }

    private async Task AppendOlderPageAsync(string personId, long? beforeUnix, long? beforeId, bool inclusive)
    {
        _isLoadingPage = true;

        try
        {
            var page = await Task
                .Run(() => conversation.Page(personId, PageSize, beforeUnix, beforeId, inclusive))
                .ConfigureAwait(true);

            // The page arrives newest-first; the view reads oldest-first. Building the page in
            // reverse and inserting it at the front puts it above what is already there, which is
            // where an older page belongs.
            var prepared = new List<ConversationItem>(page.Messages.Count * 2);

            foreach (var message in page.Messages)
            {
                // Measured against the message that comes after it in time — which, walking a
                // newest-first page, is the one already handled.
                if (_oldestLoadedUnix is { } later)
                {
                    var gap = TimeSpan.FromSeconds(later - message.SentAtUnix);

                    if (gap >= SilenceThreshold)
                    {
                        prepared.Add(new SilenceItem(gap));
                    }
                }

                prepared.Add(new MessageItem(message, LoadContextAsync));
                _oldestLoadedUnix = message.SentAtUnix;
            }

            prepared.Reverse();

            for (var i = 0; i < prepared.Count; i++)
            {
                Items.Insert(i, prepared[i]);
            }

            // The newest row of the first page is where reading forwards has to resume from.
            if (_newerUnix is null && page.Messages.Count > 0)
            {
                var newest = page.Messages[0];

                _newerUnix = newest.SentAtUnix;
                _newerId = newest.Id;
                _newestLoadedUnix = newest.SentAtUnix;
            }

            _olderUnix = page.NextBeforeUnix;
            _olderId = page.NextBeforeId;
            HasMore = page.HasMore;
        }
        finally
        {
            _isLoadingPage = false;
        }
    }

    private async Task AppendNewerPageAsync(string personId)
    {
        if (_newerUnix is not { } afterUnix || _newerId is not { } afterId)
        {
            return;
        }

        _isLoadingPage = true;

        try
        {
            var page = await Task
                .Run(() => conversation.PageAfter(personId, afterUnix, afterId, PageSize))
                .ConfigureAwait(true);

            // This page arrives in reading order already, so it goes on the end as it comes.
            foreach (var message in page.Messages)
            {
                if (_newestLoadedUnix is { } earlier)
                {
                    var gap = TimeSpan.FromSeconds(message.SentAtUnix - earlier);

                    if (gap >= SilenceThreshold)
                    {
                        Items.Add(new SilenceItem(gap));
                    }
                }

                Items.Add(new MessageItem(message, LoadContextAsync));
                _newestLoadedUnix = message.SentAtUnix;
            }

            if (page.Messages.Count > 0)
            {
                var newest = page.Messages[^1];

                _newerUnix = newest.SentAtUnix;
                _newerId = newest.Id;
            }

            HasNewer = page.HasMore;
        }
        finally
        {
            _isLoadingPage = false;
        }
    }

    private Task<IReadOnlyList<PersonMessageRow>> LoadContextAsync(long messageId) =>
        Task.Run(() => conversation.Context(messageId));
}
