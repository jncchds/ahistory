using System.Collections.ObjectModel;
using System.Globalization;
using Archive.Ai;
using Archive.Ai.Diary;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>Something in the diary's stream: the portrait, a year, a month, or a silence.</summary>
public abstract class DiaryBlock : ObservableObject;

/// <summary>One sentence, and the way back to the message it rests on.</summary>
/// <remarks>
/// A sentence is the unit of evidence (spec §8), so it is the unit of navigation too: every one of
/// them opens the conversation where it was said. Reading the source is how a diary gets checked.
/// </remarks>
public sealed class DiarySentenceItem
{
    public DiarySentenceItem(DiarySentence sentence, Action<long> open)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        ArgumentNullException.ThrowIfNull(open);

        Sentence = sentence;
        OpenCommand = new RelayCommand(() => open(sentence.MessageIds[0]), () => CanOpen);
    }

    public DiarySentence Sentence { get; }

    public string Text => Sentence.Text;

    public bool CanOpen => Sentence.MessageIds.Count > 0;

    public IRelayCommand OpenCommand { get; }
}

/// <summary>Who they are, as of the latest evidence.</summary>
public sealed class DiaryProfileBlock(IReadOnlyList<DiarySentenceItem> sentences, string? note) : DiaryBlock
{
    public IReadOnlyList<DiarySentenceItem> Sentences { get; } = sentences;

    public string? Note { get; } = note;
}

/// <summary>A year heading, with its summary when one has been written.</summary>
public sealed class DiaryYearBlock(int year, IReadOnlyList<DiarySentenceItem> summary) : DiaryBlock
{
    public string Title { get; } = year.ToString(CultureInfo.InvariantCulture);

    public IReadOnlyList<DiarySentenceItem> Summary { get; } = summary;

    public bool HasSummary => Summary.Count > 0;
}

/// <summary>
/// One month's entry — or, for a month with contact and no entry, one quiet line.
/// </summary>
/// <remarks>
/// Every month of contact is drawn, entry or not, so the timeline is complete: a diary that only
/// shows the months something happened reads as though nothing else did.
/// </remarks>
public sealed partial class DiaryMonthBlock : DiaryBlock
{
    public DiaryMonthBlock(
        string title,
        string activity,
        IReadOnlyList<DiarySentenceItem> sentences,
        IReadOnlyList<DiarySentenceItem> previous,
        int revisions,
        string? note)
    {
        Title = title;
        Activity = activity;
        Sentences = sentences;
        Previous = previous;
        Revisions = revisions;
        Note = note;
    }

    public string Title { get; }

    /// <summary>"23 messages on 6 days" — the part of a month no model wrote.</summary>
    public string Activity { get; }

    public IReadOnlyList<DiarySentenceItem> Sentences { get; }

    public bool HasEntry => Sentences.Count > 0;

    /// <summary>The text this entry replaced, when it was rewritten.</summary>
    public IReadOnlyList<DiarySentenceItem> Previous { get; }

    public int Revisions { get; }

    /// <summary>
    /// Shown as a revision, never silently: "a diary that rewrites your past every time you open it
    /// is unsettling" (spec §8), and one that says so, and shows what it said before, is not.
    /// </summary>
    public bool IsRevised => Revisions > 1;

    public string RevisedLabel => Revisions == 2 ? "revised" : $"revised {Revisions - 1} times";

    /// <summary>Something worth knowing about how the text was made — a different language, say.</summary>
    public string? Note { get; }

    [ObservableProperty]
    private bool _showsPrevious;

    [RelayCommand]
    private void TogglePrevious() => ShowsPrevious = !ShowsPrevious;
}

/// <summary>A stretch with no contact at all.</summary>
/// <remarks>§8: "four months, no contact" between entries is often the most meaningful thing there is.</remarks>
public sealed class DiaryQuietBlock(int months) : DiaryBlock
{
    public string Text { get; } = months switch
    {
        >= 24 => $"{months / 12} years, no contact",
        >= 12 => "a year and more, no contact",
        _ => $"{months} months, no contact",
    };
}

/// <summary>
/// A person's diary: a portrait, then their years, month by month, with the silences between.
/// </summary>
/// <remarks>
/// <para>
/// In the rail only while AI is on. Everything on it was written by a model, and switched off, the
/// app has no diary — not an empty one explaining what is missing (P1).
/// </para>
/// <para>
/// Nothing here writes. The diary is written in the background as its months settle; this page
/// reads what is there, says when there is nothing yet, and opens the source of any sentence.
/// </para>
/// </remarks>
public sealed partial class DiaryViewModel : ViewModelBase
{
    private readonly ArchiveQueries _queries;
    private readonly PersonConversation _conversation;
    private readonly DiaryStore _store;
    private readonly DiaryInputs _inputs;
    private readonly AiState _state;

    public DiaryViewModel(
        ArchiveQueries queries,
        PersonConversation conversation,
        DiaryStore store,
        DiaryInputs inputs,
        AiState state,
        ILogger<DiaryViewModel>? logger = null)
        : base(logger)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        _state = state ?? throw new ArgumentNullException(nameof(state));

        _state.Changed += (_, _) => OnPropertyChanged(nameof(IsAvailable));
    }

    /// <summary>Raised when a sentence's source should be opened where it was said.</summary>
    public event EventHandler<ConversationTarget>? OpenInConversationRequested;

    public override string Title => "Diary";

    public override string Glyph => "✎";

    public override int Position => 35;

    public override bool IsAvailable => _state.Current.Enabled;

    public ObservableCollection<PersonRow> People { get; } = [];

    public ObservableCollection<DiaryBlock> Blocks { get; } = [];

    /// <summary>Lets a test await the load a selection started.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private PersonRow? _selectedPerson;

    [ObservableProperty]
    private string? _personFilter;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var filter = PersonFilter;
        var people = await Task.Run(() => _queries.People(filter)).ConfigureAwait(true);
        var previous = SelectedPerson?.Id;

        People.Clear();

        foreach (var person in people)
        {
            People.Add(person);
        }

        SelectedPerson = People.FirstOrDefault(p => p.Id == previous)
            ?? People.FirstOrDefault(p => !p.IsOwner)
            ?? People.FirstOrDefault();

        await Pending.ConfigureAwait(true);
    });

    partial void OnSelectedPersonChanged(PersonRow? value) => Pending = LoadAsync(value?.Id);

    partial void OnPersonFilterChanged(string? value) => _ = RefreshAsync();

    private Task LoadAsync(string? personId) => RunAsync(async () =>
    {
        Blocks.Clear();

        if (personId is null)
        {
            IsEmpty = false;
            return;
        }

        var view = await Task.Run(() => _store.ForPerson(personId)).ConfigureAwait(true);
        var writable = await Task.Run(() => _inputs.Person(personId)).ConfigureAwait(true) is not null;

        // Another person was chosen while this one loaded.
        if (SelectedPerson?.Id != personId)
        {
            return;
        }

        foreach (var block in Build(view, _state.Current.OutputLanguage, Open))
        {
            Blocks.Add(block);
        }

        IsEmpty = view.IsEmpty;
        EmptyText = !writable
            ? "Left out of AI reading, so nothing is written about them."
            : view.Activity.Count == 0
            ? "There is nothing from them in this archive."
            : "Nothing has been written yet. Months are written once their conversations have been read.";
    });

    /// <summary>
    /// Lays a person's diary out as one stream: portrait, then each year, then its months and the
    /// silences between them.
    /// </summary>
    public static IReadOnlyList<DiaryBlock> Build(DiaryView view, string language, Action<long> open)
    {
        var blocks = new List<DiaryBlock>();

        if (view.Profile is { Latest.Sentences.Count: > 0 } profile)
        {
            blocks.Add(new DiaryProfileBlock(Items(profile.Latest.Sentences, open), Note(profile.Latest, language)));
        }

        var months = view.Months.ToDictionary(m => m.Latest.StartUnix!.Value);
        var years = view.Years.ToDictionary(y => DateTimeOffset.FromUnixTimeSeconds(y.Latest.StartUnix!.Value).Year);

        MonthActivity? previous = null;
        var year = 0;

        foreach (var month in view.Activity)
        {
            if (month.Year != year)
            {
                year = month.Year;

                blocks.Add(new DiaryYearBlock(
                    year,
                    years.TryGetValue(year, out var summary) ? Items(summary.Latest.Sentences, open) : []));
            }

            if (previous is not null)
            {
                var gap = ((month.Year - previous.Year) * 12) + month.Month - previous.Month - 1;

                if (gap >= 2)
                {
                    blocks.Add(new DiaryQuietBlock(gap));
                }
            }

            var title = month.Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            var activity = $"{month.Messages:N0} message{(month.Messages == 1 ? string.Empty : "s")} "
                + $"on {month.Days} day{(month.Days == 1 ? string.Empty : "s")}";

            blocks.Add(months.TryGetValue(month.StartUnix, out var entry)
                ? new DiaryMonthBlock(
                    title,
                    activity,
                    Items(entry.Latest.Sentences, open),
                    entry.Previous is { } earlier ? Items(earlier.Sentences, open) : [],
                    entry.Revisions,
                    Note(entry.Latest, language))
                : new DiaryMonthBlock(title, activity, [], [], 0, null));

            previous = month;
        }

        return blocks;
    }

    private static List<DiarySentenceItem> Items(IReadOnlyList<DiarySentence> sentences, Action<long> open) =>
        [.. sentences.Select(s => new DiarySentenceItem(s, open))];

    /// <summary>
    /// Said when an entry is in another language than the one now chosen.
    /// </summary>
    /// <remarks>
    /// Changing the output language does not rewrite a decade (ai-plan.md §8); it marks what was
    /// written before, so the difference is explained rather than looking like a glitch.
    /// </remarks>
    private static string? Note(DiaryEntry entry, string language) =>
        entry.Language is { } written && !string.Equals(written, language, StringComparison.OrdinalIgnoreCase)
            ? $"written in {written}"
            : null;

    /// <summary>Opens the conversation a sentence's message was said in.</summary>
    private async void Open(long messageId)
    {
        try
        {
            var personId = await Task.Run(() => _conversation.PersonOf(messageId)).ConfigureAwait(true)
                ?? SelectedPerson?.Id;

            if (personId is not null)
            {
                OpenInConversationRequested?.Invoke(this, new ConversationTarget(personId, messageId));
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Opening a diary sentence's source failed.");
            Error = ex.Message;
        }
    }
}
