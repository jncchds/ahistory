using System.Collections.ObjectModel;
using Archive.Ai;
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

    public AiSettingsViewModel(
        AiState state,
        AiClient client,
        AiConnectionCheck check,
        ILogger<AiSettingsViewModel>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _check = check ?? throw new ArgumentNullException(nameof(check));

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
        OutputLanguage = settings.OutputLanguage;
        RecordPromptBodies = settings.RecordPromptBodies;
        MaxParallelCalls = settings.MaxParallelCalls;
        TimeoutSeconds = Math.Max(1, settings.TimeoutMs / 1000);
        DisclaimerAccepted = settings.DisclaimerIsCurrent;
    }
}
