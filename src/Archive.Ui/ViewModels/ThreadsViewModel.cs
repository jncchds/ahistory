using System.Collections.ObjectModel;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archive.Ui.ViewModels;

/// <summary>
/// Conversations as they were stored, one thread at a time.
/// </summary>
/// <remarks>
/// M5 replaces this with the per-person continuous conversation — the union of someone's DM
/// thread and the group messages they sent. This page stays afterwards: when a group line reads
/// as nonsense out of context, the thread it came from is where you go to read around it (§4).
/// </remarks>
public sealed partial class ThreadsViewModel(ArchiveQueries queries) : ViewModelBase
{
    /// <summary>Page size. Small enough that the first screen is instant on a large thread.</summary>
    private const int PageSize = 100;

    public override string Title => "Conversations";

    public ObservableCollection<ThreadRow> Threads { get; } = [];

    public ObservableCollection<MessageRow> Messages { get; } = [];

    [ObservableProperty]
    private ThreadRow? _selectedThread;

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
            var threads = await Task.Run(queries.Threads).ConfigureAwait(true);

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

    private Task LoadFirstPageAsync() => RunAsync(async () =>
    {
        Messages.Clear();
        _beforeUnix = null;
        _beforeId = null;
        HasMore = false;

        if (SelectedThread is null)
        {
            return;
        }

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

        foreach (var message in page.Messages)
        {
            Messages.Add(message);
        }

        _beforeUnix = page.NextBeforeUnix;
        _beforeId = page.NextBeforeId;
        HasMore = page.HasMore;
    }
}
