using System.Collections.ObjectModel;
using Archive.Ai;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>
/// Conversations as they were stored, one thread at a time.
/// </summary>
/// <remarks>
/// <para>
/// The per-person view (§4) is the app's answer to "what did we say to each other", and it is a
/// query across threads. This page is the answer to a different question — "what was said in that
/// room" — and a group conversation only exists here: the per-person stream carries the messages
/// one person sent in a group, never the group itself.
/// </para>
/// <para>
/// So it shows who is in the room, and who said what. Ten people in a group rendered as ten
/// identical left-aligned bubbles is a transcript, not a conversation.
/// </para>
/// </remarks>
public sealed partial class ThreadsViewModel(
    ArchiveQueries queries,
    ILogger<ThreadsViewModel>? logger = null,
    AiExclusions? exclusions = null,
    AiState? state = null)
    : ViewModelBase(logger)
{
    /// <summary>Whether this conversation can be left out of AI reading from here — only while AI is on.</summary>
    [ObservableProperty]
    private bool _canExcludeThread;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThreadExclusionLabel))]
    private bool _isThreadExcluded;

    public string ThreadExclusionLabel => IsThreadExcluded ? "Include in AI reading" : "Leave out of AI reading";

    /// <summary>
    /// Leaves this conversation out of everything a model reads, or lets it back in.
    /// </summary>
    /// <remarks>
    /// For a room rather than a person: a group that is nobody's business, or one direct thread
    /// with someone whose others are fine. With a hosted endpoint, left out also means never sent.
    /// </remarks>
    [RelayCommand]
    private Task ToggleThreadExclusion() => RunAsync(async () =>
    {
        if (exclusions is null || SelectedThread is not { } thread)
        {
            return;
        }

        var excluded = !IsThreadExcluded;

        await Task.Run(() => exclusions.SetThread(thread.Id, excluded)).ConfigureAwait(true);

        IsThreadExcluded = excluded;
    });
    /// <summary>Page size. Small enough that the first screen is instant on a large thread.</summary>
    private const int PageSize = 100;

    public override string Title => "Threads";

    public override string Glyph => "❐";

    public override int Position => 60;

    public ObservableCollection<ThreadRow> Threads { get; } = [];

    /// <summary>
    /// Oldest first, so the conversation reads downward the way a chat does.
    /// </summary>
    /// <remarks>
    /// Pages are fetched newest-first — that is what keyset paging backwards from the present
    /// gives — and inserted at the front. The two orders are opposite on purpose: fetching newest
    /// first is what makes opening a ten-year thread instant, reading oldest first is what makes
    /// it a conversation rather than a log. Same arrangement as <see cref="PersonViewModel"/>.
    /// </remarks>
    public ObservableCollection<ThreadMessageItem> Messages { get; } = [];

    /// <summary>Who is in the selected conversation, whether or not they ever spoke.</summary>
    public ObservableCollection<ThreadParticipantRow> Participants { get; } = [];

    [ObservableProperty]
    private ThreadRow? _selectedThread;

    [ObservableProperty]
    private string? _threadFilter;

    [ObservableProperty]
    private bool _hasMore;

    private long? _beforeUnix;
    private long? _beforeId;

    /// <summary>
    /// The load kicked off by the current selection.
    /// </summary>
    /// <remarks>
    /// Selecting a conversation starts a load from a property setter, which cannot be awaited.
    /// Keeping the task means callers that need the messages to be there — a refresh, a test —
    /// can wait for it, instead of racing a fire-and-forget continuation.
    /// </remarks>
    private Task _pendingLoad = Task.CompletedTask;

    public override async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            var filter = ThreadFilter;
            var threads = await Task.Run(() => queries.Threads(filter)).ConfigureAwait(true);

            var previous = SelectedThread?.Id;

            Threads.Clear();

            foreach (var thread in threads)
            {
                Threads.Add(thread);
            }

            // Keep the reader where they were across a refresh — an import finishing should not
            // bounce them out of the conversation they were reading.
            SelectedThread = Threads.FirstOrDefault(t => t.Id == previous) ?? Threads.FirstOrDefault();
        }).ConfigureAwait(true);

        await _pendingLoad.ConfigureAwait(true);
    }

    partial void OnSelectedThreadChanged(ThreadRow? value) => _pendingLoad = LoadFirstPageAsync();

    partial void OnThreadFilterChanged(string? value) => _ = RefreshAsync();

    private Task LoadFirstPageAsync() => RunAsync(async () =>
    {
        Messages.Clear();
        Participants.Clear();
        _beforeUnix = null;
        _beforeId = null;
        HasMore = false;

        var thread = SelectedThread;

        if (thread is null)
        {
            return;
        }

        foreach (var participant in await Task.Run(() => queries.ThreadParticipants(thread.Id))
                     .ConfigureAwait(true))
        {
            Participants.Add(participant);
        }

        // Only offered while AI is on: switched off, the window carries no trace of it (P1).
        CanExcludeThread = exclusions is not null && state?.Current.Enabled == true;
        IsThreadExcluded = CanExcludeThread
            && await Task.Run(() => exclusions!.IsThreadExcluded(thread.Id)).ConfigureAwait(true);

        await AppendPageAsync().ConfigureAwait(true);
    });

    /// <summary>
    /// Loads the next older page.
    /// </summary>
    /// <remarks>
    /// Keyset paging, so this stays constant-time however far back the reader scrolls, and a
    /// background import inserting underneath cannot shift the page boundaries.
    /// </remarks>
    [RelayCommand]
    private Task LoadMore() => RunAsync(AppendPageAsync);

    private async Task AppendPageAsync()
    {
        var thread = SelectedThread;

        if (thread is null)
        {
            return;
        }

        var (unix, id) = (_beforeUnix, _beforeId);

        var page = await Task.Run(() => queries.ThreadMessages(thread.Id, PageSize, unix, id))
            .ConfigureAwait(true);

        // The page arrives newest-first and reads oldest-first, so it is reversed before the runs
        // of one speaker are worked out — a run is a property of reading order, not of fetch order.
        var isGroup = IsGroupConversation(thread);
        var ordered = page.Messages.Reverse().ToArray();
        var items = ThreadMessageItem.Build(ordered, previous: null, isGroup: isGroup);

        // What was at the top of the list is now preceded by this page's last message, so whether
        // it still opens a run has changed. Recomputing it is one item; not recomputing it leaves
        // a stray name in the middle of a run, right where "load older" was pressed.
        var boundary = Messages.FirstOrDefault();

        for (var i = 0; i < items.Count; i++)
        {
            Messages.Insert(i, items[i]);
        }

        if (boundary is not null && ordered.Length > 0)
        {
            Messages[items.Count] = ThreadMessageItem
                .Build([boundary.Row], ordered[^1], isGroup)[0];
        }

        _beforeUnix = page.NextBeforeUnix;
        _beforeId = page.NextBeforeId;
        HasMore = page.HasMore;
    }

    /// <summary>
    /// Whether senders need naming in this conversation.
    /// </summary>
    /// <remarks>
    /// A group is a group even when only two people ever spoke in it — who said a thing is the
    /// question a room raises, and the roster is a record of who could have. The participant count
    /// is the second test rather than the first, for a direct thread that turns out to have more
    /// than two accounts in it.
    /// </remarks>
    private static bool IsGroupConversation(ThreadRow thread) =>
        thread.IsGroup || thread.ParticipantCount > 2;
}
