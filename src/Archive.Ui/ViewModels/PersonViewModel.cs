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

    [ObservableProperty]
    private bool _hasMore;

    private long? _beforeUnix;
    private long? _beforeId;
    private long? _oldestLoadedUnix;
    private Task _pendingLoad = Task.CompletedTask;

    public override async Task RefreshAsync()
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

    partial void OnSelectedPersonChanged(PersonRow? value) => _pendingLoad = LoadFirstPageAsync();

    partial void OnPersonFilterChanged(string? value) => _ = RefreshAsync();

    private Task LoadFirstPageAsync() => RunAsync(async () =>
    {
        Items.Clear();
        _beforeUnix = null;
        _beforeId = null;
        _oldestLoadedUnix = null;
        HasMore = false;

        if (SelectedPerson is not null)
        {
            await AppendPageAsync().ConfigureAwait(true);
        }
    });

    [RelayCommand]
    private Task LoadMore() => RunAsync(AppendPageAsync);

    private async Task AppendPageAsync()
    {
        var person = SelectedPerson;

        if (person is null)
        {
            return;
        }

        var (unix, id) = (_beforeUnix, _beforeId);

        var page = await Task.Run(() => conversation.Page(person.Id, PageSize, unix, id))
            .ConfigureAwait(true);

        // The page arrives newest-first; the view reads oldest-first. Building the page in reverse
        // and inserting it at the front puts it above what is already there, which is where an
        // older page belongs.
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

        _beforeUnix = page.NextBeforeUnix;
        _beforeId = page.NextBeforeId;
        HasMore = page.HasMore;
    }

    private Task<IReadOnlyList<PersonMessageRow>> LoadContextAsync(long messageId) =>
        Task.Run(() => conversation.Context(messageId));
}
