using System.Collections.ObjectModel;
using Archive.Ai;
using Archive.Ai.Jobs;
using Archive.Ai.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>One provider family, as the picker shows it.</summary>
public sealed record ProviderOption(LlmProviderKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Where AI is switched on, pointed at a model, and tested.
/// </summary>
/// <remarks>
/// <para>
/// The only AI page that is always in the rail, because it is where the feature is turned on and
/// a switch you cannot reach is not a switch. Everything else AI adds — statistics now, facts and
/// the diary later — appears only once it is enabled.
/// </para>
/// <para>
/// Edits are held here and written on Save rather than as they are typed. A settings object that
/// updated live would have the app talking to a half-typed endpoint on the way to a complete one.
/// </para>
/// </remarks>
public sealed partial class AiSettingsViewModel : ViewModelBase
{
    /// <summary>
    /// What enabling this actually means, in the two sentences a user cannot work out themselves.
    /// </summary>
    /// <remarks>
    /// Accuracy depends on the model they chose, and whether their correspondence leaves the
    /// machine depends on the endpoint they chose. Reword this and
    /// <see cref="AiSettings.CurrentDisclaimerVersion"/> has to go up, or people who agreed to the
    /// old wording are recorded as having agreed to the new one.
    /// </remarks>
    public const string Disclaimer =
        "Everything on these pages is produced by a language model reading your messages. "
        + "It will be confidently wrong about some of it, and how wrong depends on the model you "
        + "choose. Facts carry the messages they came from — check them. "
        + "If you configure a hosted provider, your messages are sent to it.";

    private readonly AiState _state;
    private readonly AiClient _client;
    private readonly AiConnectionCheck _check;
    private readonly AiForget? _forget;
    private readonly AiRunner? _runner;
    private readonly AiExclusions? _exclusions;
    private readonly EmbeddingStore? _embeddings;

    public AiSettingsViewModel(
        AiState state,
        AiClient client,
        AiConnectionCheck check,
        AiForget? forget = null,
        AiRunner? runner = null,
        ILogger<AiSettingsViewModel>? logger = null,
        AiExclusions? exclusions = null,
        EmbeddingStore? embeddings = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _forget = forget;
        _runner = runner;
        _exclusions = exclusions;
        _embeddings = embeddings;

        Load(state.Current);
    }

    public override string Title => "AI";

    public override string Glyph => "✷";

    public override int Position => 70;

    public IReadOnlyList<ProviderOption> Providers { get; } =
    [
        new(LlmProviderKind.OpenAiCompatible, "OpenAI-compatible"),
        new(LlmProviderKind.Ollama, "Ollama"),
        new(LlmProviderKind.OpenAi, "OpenAI"),
        new(LlmProviderKind.GoogleAiStudio, "Google AI Studio"),
    ];

    /// <summary>
    /// What the model boxes offer, from the last catalogue fetch.
    /// </summary>
    /// <remarks>
    /// Suggestions, never a closed list. A gateway routinely serves models it does not advertise,
    /// and a dropdown built from <c>/models</c> turns that into "your model does not exist".
    /// </remarks>
    public ObservableCollection<string> ModelSuggestions { get; } = [];

    /// <summary>Lets a test await the work a command started.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private ProviderOption? _provider;

    [ObservableProperty]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _mainModel = string.Empty;

    [ObservableProperty]
    private string _utilityModel = string.Empty;

    [ObservableProperty]
    private string _embeddingModel = string.Empty;

    [ObservableProperty]
    private string _visionModel = string.Empty;

    [ObservableProperty]
    private string _transcriptionModel = string.Empty;

    /// <summary>Tokens per 24 hours; 0 for no cap.</summary>
    [ObservableProperty]
    private long _dailyTokenBudget;

    /// <summary>
    /// What changing the embedding model will do, said before it is saved (ai-plan.md §9.2).
    /// </summary>
    /// <remarks>
    /// Not "your index will be lost": it will not be. The old vectors keep answering until the new
    /// ones are complete. What the user needs to know is that the rebuild happens, in the background,
    /// and that nothing breaks meanwhile.
    /// </remarks>
    [ObservableProperty]
    private string? _embeddingNote;

    /// <summary>Vectors from models that are neither configured nor in use.</summary>
    [ObservableProperty]
    private long _unusedVectors;

    /// <summary>This archive has said no to being read by a model, on any machine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptOutLabel))]
    private bool _archiveOptedOut;

    public bool CanOptOut => _exclusions is not null;

    public string OptOutLabel =>
        ArchiveOptedOut ? "Let AI read this archive again" : "Never read this archive with AI";

    [ObservableProperty]
    private string _outputLanguage = "English";

    [ObservableProperty]
    private bool _recordPromptBodies;

    [ObservableProperty]
    private int _maxParallelCalls = 2;

    /// <summary>
    /// How long one call may take, in seconds; stored as milliseconds.
    /// </summary>
    /// <remarks>
    /// On the page because a local model on a hard drive can need minutes for the first answer
    /// after it was unloaded, and a limit only reachable by editing a file is not one a user can
    /// act on when the error tells them to.
    /// </remarks>
    [ObservableProperty]
    private int _timeoutSeconds = 120;

    /// <summary>Ticked by the user, not by us. Cleared when the wording changes.</summary>
    [ObservableProperty]
    private bool _disclaimerAccepted;

    /// <summary>The result of Test connection, or of the last save.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>True when the last check found a model that can run the pipeline.</summary>
    [ObservableProperty]
    private bool _statusIsGood;

    /// <summary>Where the settings file is, so a user can find it.</summary>
    public string SettingsPath => _state.SettingsPath;

    /// <summary>
    /// Whether Save would do anything sensible.
    /// </summary>
    /// <remarks>
    /// Switching AI on with no model, or without having read the disclaimer, is not a state worth
    /// storing — the background runner would start against a blank model name.
    /// </remarks>
    public bool CanSave =>
        !Enabled || (DisclaimerAccepted && !string.IsNullOrWhiteSpace(MainModel));

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    partial void OnMainModelChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnDisclaimerAcceptedChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    partial void OnEmbeddingModelChanged(string value) => _ = DescribeEmbeddingsAsync();

    /// <summary>
    /// Leaves this whole archive out, or lets it back in.
    /// </summary>
    /// <remarks>
    /// Written into the save, not the settings file, so it travels with the archive: a save built
    /// from someone else's correspondence stays unread on whichever machine it is opened.
    /// </remarks>
    [RelayCommand]
    private Task ToggleOptOut() => Pending = RunAsync(async () =>
    {
        if (_exclusions is null)
        {
            return;
        }

        var optedOut = !ArchiveOptedOut;

        await Task.Run(() => _exclusions.SetSaveOptOut(optedOut)).ConfigureAwait(true);

        ArchiveOptedOut = optedOut;
        Status = optedOut
            ? "Nothing in this archive will be read by a model, on this machine or any other."
            : "This archive can be read again. What was skipped is back in the queue.";
        StatusIsGood = true;
    });

    [RelayCommand]
    private Task ClearUnusedVectors() => Pending = RunAsync(async () =>
    {
        if (_embeddings is null)
        {
            return;
        }

        var configured = _state.Current.EmbeddingModel;
        var cleared = await Task.Run(() => _embeddings.ClearUnused(configured)).ConfigureAwait(true);

        Status = $"Cleared {cleared:N0} vector(s) from models no longer in use.";
        StatusIsGood = true;

        await DescribeEmbeddingsAsync().ConfigureAwait(true);
    });

    private async Task DescribeEmbeddingsAsync()
    {
        if (_embeddings is null)
        {
            return;
        }

        var saved = _state.Current.EmbeddingModel.Trim();
        var typed = EmbeddingModel.Trim();

        var (note, unused) = await Task.Run(() =>
        {
            var models = _embeddings.Models();
            var indexed = models.FirstOrDefault(m => m.Model == saved).Count;

            string? text = null;

            if (!string.Equals(typed, saved, StringComparison.Ordinal) && indexed > 0)
            {
                text = typed.Length == 0
                    ? $"{indexed:N0} conversation(s) are indexed with {saved}. Leaving this empty turns search by meaning off; the vectors stay until they are cleared below."
                    : $"{indexed:N0} conversation(s) are indexed with {saved}. {typed} will be built in the background and takes over when it is complete — search keeps using {saved} until then.";
            }

            var keep = new[] { saved, _embeddings.Active(saved) };

            return (text, models.Where(m => !keep.Contains(m.Model)).Sum(m => m.Count));
        }).ConfigureAwait(true);

        EmbeddingNote = note;
        UnusedVectors = unused;
    }

    [RelayCommand]
    private Task LoadModels() => Pending = RunAsync(async () =>
    {
        var models = await _client.ListModelsAsync(Draft()).ConfigureAwait(true);

        ModelSuggestions.Clear();

        foreach (var model in models)
        {
            ModelSuggestions.Add(model.Id);
        }

        Status = models.Count == 0
            ? "The endpoint answered, but listed no models. Type the name instead."
            : $"{models.Count} model(s) offered.";

        StatusIsGood = models.Count > 0;
    });

    [RelayCommand]
    private Task TestConnection() => Pending = RunAsync(async () =>
    {
        var draft = Draft();

        // Said before the call rather than after it: a model that has to load from a hard drive
        // first can take minutes, and a spinner with no words beside it reads as a hang.
        Status = $"Asking {draft.ModelFor(AiWorkKind.Main)}… if it has to load first, this can take a few minutes.";
        StatusIsGood = false;

        var result = await _check.RunAsync(draft).ConfigureAwait(true);

        Status = result.Detail;
        StatusIsGood = result.IsUsable;
    });

    [RelayCommand]
    private Task Save() => Pending = RunAsync(() =>
    {
        if (!CanSave)
        {
            throw new InvalidOperationException(
                "Switching AI on needs a main model and the note above acknowledged.");
        }

        var settings = Draft();

        // Recorded at the moment of consent, and only then: a version stamped on a settings save
        // the user made for some other reason would claim they had read something they had not.
        settings.DisclaimerAcknowledgedVersion = DisclaimerAccepted
            ? AiSettings.CurrentDisclaimerVersion
            : 0;

        _state.Update(settings);

        Status = settings.Enabled ? "AI is on." : "AI is off.";
        StatusIsGood = true;

        return Task.CompletedTask;
    });

    /// <summary>The word that has to be typed before everything is forgotten.</summary>
    public const string ForgetWord = "forget";

    /// <summary>
    /// Whether the page can offer to forget.
    /// </summary>
    /// <remarks>
    /// On this page, and not on the activity page, because this one is there with AI switched off —
    /// which is exactly when someone is most likely to want everything it produced gone.
    /// </remarks>
    public bool CanOfferForget => _forget is not null;

    [ObservableProperty]
    private string _forgetSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanForget))]
    private string _forgetConfirmation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanForget))]
    private bool _hasSomethingToForget;

    /// <summary>
    /// Typed, not clicked (ai-plan.md §11.4).
    /// </summary>
    /// <remarks>
    /// An OK button is one misplaced click, and this discards what may be days of reading.
    /// </remarks>
    public bool CanForget =>
        HasSomethingToForget
        && string.Equals(ForgetConfirmation.Trim(), ForgetWord, StringComparison.OrdinalIgnoreCase);

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        await LoadForgetCountsAsync().ConfigureAwait(true);
        await DescribeEmbeddingsAsync().ConfigureAwait(true);

        if (_exclusions is not null)
        {
            ArchiveOptedOut = await Task.Run(_exclusions.SaveOptedOut).ConfigureAwait(true);
        }
    });

    [RelayCommand]
    private Task ForgetEverything() => Pending = RunAsync(async () =>
    {
        if (_forget is null || !CanForget)
        {
            throw new InvalidOperationException($"Type \"{ForgetWord}\" to confirm.");
        }

        // Stopped first, so nothing is written back into what is being cleared.
        if (_runner is not null)
        {
            await _runner.PauseAsync().ConfigureAwait(true);
        }

        var removed = await Task.Run(_forget.Everything).ConfigureAwait(true);

        ForgetConfirmation = string.Empty;
        Status = $"Forgotten: {removed.Facts:N0} fact(s), {removed.DiaryTexts:N0} diary text(s), "
            + $"{removed.Vectors:N0} vector(s), {removed.Sessions:N0} session(s) and "
            + $"{removed.Calls:N0} recorded call(s). Every message is exactly as it was."
            + (_state.Current.Enabled
                ? " AI is still on, so the archive will be read again the next time work is looked for."
                : string.Empty);
        StatusIsGood = true;

        await LoadForgetCountsAsync().ConfigureAwait(true);
    });

    private async Task LoadForgetCountsAsync()
    {
        if (_forget is null)
        {
            return;
        }

        var counts = await Task.Run(_forget.Counts).ConfigureAwait(true);

        HasSomethingToForget = !counts.IsEmpty;
        ForgetSummary = counts.IsEmpty
            ? "Nothing to forget: the AI layer has produced nothing in this archive."
            : $"{counts.Facts:N0} fact(s), {counts.DiaryTexts:N0} diary text(s), {counts.Vectors:N0} vector(s), "
              + $"{counts.MediaTexts:N0} transcript(s) and image text(s), {counts.Sessions:N0} session(s), "
              + $"{counts.Calls:N0} recorded call(s) and {counts.Jobs:N0} queued job(s). "
              + "Every message, media file and keyword search stays.";
    }

    /// <summary>Puts the form back to what is stored, discarding edits.</summary>
    [RelayCommand]
    private void Revert()
    {
        Load(_state.Current);

        Status = null;
        StatusIsGood = false;
    }

    /// <summary>The settings as the form currently reads them.</summary>
    private AiSettings Draft()
    {
        var settings = _state.Current.Clone();

        settings.Enabled = Enabled;
        settings.Provider = Provider?.Kind ?? LlmProviderKind.OpenAiCompatible;
        settings.Endpoint = Endpoint.Trim();
        settings.ApiKey = ApiKey;
        settings.MainModel = MainModel.Trim();
        settings.UtilityModel = UtilityModel.Trim();
        settings.EmbeddingModel = EmbeddingModel.Trim();
        settings.VisionModel = VisionModel.Trim();
        settings.TranscriptionModel = TranscriptionModel.Trim();
        settings.DailyTokenBudget = Math.Max(0, DailyTokenBudget);
        settings.OutputLanguage = string.IsNullOrWhiteSpace(OutputLanguage) ? "English" : OutputLanguage.Trim();
        settings.RecordPromptBodies = RecordPromptBodies;
        settings.MaxParallelCalls = Math.Max(1, MaxParallelCalls);
        settings.TimeoutMs = Math.Max(10, TimeoutSeconds) * 1000;

        return settings;
    }

    private void Load(AiSettings settings)
    {
        Enabled = settings.Enabled;
        Provider = Providers.FirstOrDefault(p => p.Kind == settings.Provider) ?? Providers[0];
        Endpoint = settings.Endpoint;
        ApiKey = settings.ApiKey;
        MainModel = settings.MainModel;
        UtilityModel = settings.UtilityModel;
        EmbeddingModel = settings.EmbeddingModel;
        VisionModel = settings.VisionModel;
        TranscriptionModel = settings.TranscriptionModel;
        DailyTokenBudget = settings.DailyTokenBudget;
        OutputLanguage = settings.OutputLanguage;
        RecordPromptBodies = settings.RecordPromptBodies;
        MaxParallelCalls = settings.MaxParallelCalls;
        TimeoutSeconds = Math.Max(1, settings.TimeoutMs / 1000);
        DisclaimerAccepted = settings.DisclaimerIsCurrent;
    }
}
