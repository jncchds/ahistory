using System.Collections.ObjectModel;
using System.Globalization;
using Archive.Ai;
using Archive.Ai.Extraction;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ui.ViewModels;

/// <summary>
/// One fact in the panel, with the controls to check it and correct it.
/// </summary>
/// <remarks>
/// Every fact is one click from the message it came from. That is not decoration: the citation
/// rule catches a model that cites nothing, and only a person reading the source catches one that
/// cites the right message and says the wrong thing (ai-plan.md §5.4).
/// </remarks>
public sealed partial class FactItem : ObservableObject
{
    private readonly FactsPanelViewModel _owner;

    public FactItem(FactView fact, FactsPanelViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(fact);

        Fact = fact;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _editClaim = fact.ClaimText;
        _editObject = fact.ObjectText;
    }

    public FactView Fact { get; }

    public string Claim => Fact.ClaimText;

    /// <summary>
    /// Where the claim came from, in the words a reader needs to weigh it.
    /// </summary>
    /// <remarks>
    /// Self-reported and reflected are different kinds of evidence (§7), and a line said in a group
    /// is weaker than one said privately (§4). Saying which, on the fact, is what lets a reader
    /// discount the right ones rather than trusting or doubting the whole panel at once.
    /// </remarks>
    public string Detail
    {
        get
        {
            var parts = new List<string>
            {
                Fact.OtherPersonName is { } other ? $"with {other}" : Fact.Predicate.Replace('_', ' '),
                Fact.EvidenceKind == "self_report" ? "in their own words" : "said to or about them",
            };

            if (Fact.OriginKind == "group")
            {
                parts.Add("in a group");
            }

            if (Fact.ValidFromUtc is { Length: >= 7 } from)
            {
                parts.Add($"since {from[..7]}");
            }

            parts.Add(Fact.Source == "user_edited"
                ? "corrected by you"
                : Fact.Confidence.ToString("P0", CultureInfo.InvariantCulture));

            return string.Join(" · ", parts);
        }
    }

    public bool CanOpen => Fact.FirstMessageId is not null;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editClaim;

    [ObservableProperty]
    private string _editObject;

    [RelayCommand]
    private void Open()
    {
        if (Fact.FirstMessageId is { } messageId)
        {
            _owner.Reveal(messageId);
        }
    }

    [RelayCommand]
    private void BeginEdit()
    {
        EditClaim = Fact.ClaimText;
        EditObject = Fact.ObjectText;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private Task SaveEdit() => _owner.EditAsync(this, EditObject, EditClaim);

    [RelayCommand]
    private Task Delete() => _owner.DeleteAsync(this);
}

/// <summary>
/// What is known about the person being read, beside their conversation.
/// </summary>
/// <remarks>
/// <para>
/// Not a page, and only there when AI is on: switched off, the conversation fills the window as it
/// always did, with no panel explaining what is missing (P1).
/// </para>
/// <para>
/// An empty panel says <i>why</i> it is empty, because "nothing has been read yet" and "everything
/// read here was arrangements" look identical otherwise, and the difference is the whole question
/// a reader has (ai-plan.md §7).
/// </para>
/// </remarks>
public sealed partial class FactsPanelViewModel : ObservableObject
{
    private readonly FactStore _store;
    private readonly AiCoverage _coverage;
    private readonly AiState _state;
    private readonly ILogger _log;
    private readonly AiExclusions? _exclusions;

    private string? _personId;

    public FactsPanelViewModel(
        FactStore store,
        AiCoverage coverage,
        AiState state,
        AiExclusions? exclusions = null,
        ILogger<FactsPanelViewModel>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _log = logger ?? NullLogger<FactsPanelViewModel>.Instance;
        _exclusions = exclusions;

        _state.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsAvailable));
            _ = ShowAsync(_personId);
        };
    }

    /// <summary>Raised with the message a fact was read from, for the conversation to open.</summary>
    public event EventHandler<long>? RevealRequested;

    public bool IsAvailable => _state.Current.Enabled;

    public ObservableCollection<FactItem> Facts { get; } = [];

    /// <summary>Lets a test await the work a command started.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private string _coverageLine = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    [ObservableProperty]
    private string? _error;

    /// <summary>This person's correspondence is not read by a model, nor sent anywhere.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExclusionLabel))]
    private bool _isExcluded;

    public bool CanExclude => _exclusions is not null;

    public string ExclusionLabel => IsExcluded ? "Include in AI reading" : "Leave out of AI reading";

    /// <summary>
    /// Leaves this person out, or lets them back in.
    /// </summary>
    /// <remarks>
    /// A button rather than a checkbox bound both ways: loading the panel sets the flag from the
    /// save, and a two-way binding would write that straight back as though the user had clicked.
    /// </remarks>
    [RelayCommand]
    private Task ToggleExclusion() => Pending = GuardAsync(async () =>
    {
        if (_exclusions is null || _personId is null)
        {
            return;
        }

        var personId = _personId;
        var excluded = !IsExcluded;

        await Task.Run(() => _exclusions.Set(personId, excluded)).ConfigureAwait(true);
        await LoadAsync(personId).ConfigureAwait(true);
    });

    /// <summary>Loads what is known about a person, or clears the panel for nobody.</summary>
    public Task ShowAsync(string? personId)
    {
        _personId = personId;

        return Pending = LoadAsync(personId);
    }

    [RelayCommand]
    private Task Refresh() => ShowAsync(_personId);

    internal void Reveal(long messageId) => RevealRequested?.Invoke(this, messageId);

    /// <summary>
    /// The sessions among these that a model has yet to read — nothing at all while AI is off.
    /// </summary>
    public async Task<IReadOnlySet<string>> UnreadAsync(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        if (!IsAvailable || sessionIds.Count == 0)
        {
            return new HashSet<string>();
        }

        try
        {
            return await Task.Run(() => _coverage.Unread(sessionIds)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A mark in the margin is not worth failing a conversation over.
            _log.LogWarning(ex, "Could not work out which sessions are unread.");

            return new HashSet<string>();
        }
    }

    internal Task EditAsync(FactItem item, string objectText, string claimText) =>
        Pending = GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(objectText) || string.IsNullOrWhiteSpace(claimText))
            {
                throw new InvalidOperationException("A correction needs both a value and a sentence.");
            }

            await Task.Run(() => _store.Edit(item.Fact.Id, objectText, claimText)).ConfigureAwait(true);
            await LoadAsync(_personId).ConfigureAwait(true);
        });

    internal Task DeleteAsync(FactItem item) =>
        Pending = GuardAsync(async () =>
        {
            await Task.Run(() => _store.Delete(item.Fact.Id)).ConfigureAwait(true);
            await LoadAsync(_personId).ConfigureAwait(true);
        });

    private Task LoadAsync(string? personId) => GuardAsync(async () =>
    {
        Facts.Clear();

        if (personId is null || !IsAvailable)
        {
            CoverageLine = string.Empty;
            IsEmpty = false;

            return;
        }

        var facts = await Task.Run(() => _store.ForPerson(personId)).ConfigureAwait(true);
        var coverage = await Task.Run(() => _coverage.ForPerson(personId)).ConfigureAwait(true);
        var excluded = _exclusions is not null
            && await Task.Run(() => _exclusions.IsExcluded(personId)).ConfigureAwait(true);

        // The person changed while this was loading; what arrived belongs to someone else.
        if (personId != _personId)
        {
            return;
        }

        foreach (var fact in facts)
        {
            Facts.Add(new FactItem(fact, this));
        }

        IsExcluded = excluded;

        CoverageLine = excluded
            ? "Left out: nothing they wrote is read by a model or sent anywhere."
            : coverage.Substantive == 0
            ? "None of these conversations looked worth reading closely."
            : $"{coverage.Extracted:N0} of {coverage.Substantive:N0} conversation(s) worth reading have been read.";

        IsEmpty = Facts.Count == 0;
        EmptyText = coverage.Extracted == 0
            ? "Nothing has been read here yet."
            : "Nothing recorded — what has been read so far was arrangements rather than news.";
    });

    private async Task GuardAsync(Func<Task> work)
    {
        Error = null;

        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The facts panel failed.");
            Error = ex.Message;
        }
    }
}
