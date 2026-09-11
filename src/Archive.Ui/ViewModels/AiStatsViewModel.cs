using System.Collections.ObjectModel;
using System.Globalization;
using Archive.Ai;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>One line of the statistics table.</summary>
public sealed record AiStatRow(string Purpose, string Calls, string Failures, string Tokens, string Median);

/// <summary>
/// What the model has been asked to do, how far through the archive it is, and what is failing.
/// </summary>
/// <remarks>
/// <para>
/// In the rail only while AI is enabled. Statistics about a feature nobody switched on are an
/// empty page explaining what they are missing, which is the thing P1 refuses to have.
/// </para>
/// <para>
/// There is no money column. Prices depend on an endpoint the user configured and change without
/// notice, so a figure here would be a confident wrong number about their bill — tokens are a
/// fact, cost is a guess.
/// </para>
/// </remarks>
public sealed partial class AiStatsViewModel : ViewModelBase
{
    /// <summary>
    /// How often the page may redraw while work is running.
    /// </summary>
    /// <remarks>
    /// The runner finishes a segmentation job every few milliseconds on a small archive. Redrawing
    /// per job would put the UI thread in a loop counting rows instead of drawing them, which is a
    /// progress display that slows down the thing it reports on.
    /// </remarks>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// How many extraction calls have to have happened before their average is worth quoting.
    /// </summary>
    /// <remarks>
    /// Before that the confirmation says the cost is unknown. A made-up estimate that is wrong by
    /// ten times is worse than no estimate, because it is the number people decide on.
    /// </remarks>
    private const int CallsBeforeEstimating = 50;

    private readonly AiState _state;
    private readonly AiInteractions _interactions;
    private readonly AiCoverage _coverage;
    private readonly AiJobs _jobs;
    private readonly AiRunner _runner;
    private readonly AiWork _work;
    private readonly ILlmProviderFactory _factory;

    private DateTime _lastRefresh = DateTime.MinValue;

    /// <summary>
    /// The user said "not now" this session.
    /// </summary>
    /// <remarks>
    /// The page refreshes on every progress tick. Without this, declining would last half a
    /// second before the question opened again.
    /// </remarks>
    private bool _declined;

    public AiStatsViewModel(
        AiState state,
        AiInteractions interactions,
        AiCoverage coverage,
        AiJobs jobs,
        AiRunner runner,
        AiWork work,
        ILlmProviderFactory factory,
        ILogger<AiStatsViewModel>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
        _interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
        _coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

        // The rail follows the switch immediately rather than at the next restart.
        _state.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(RecordsPromptBodies));
            _ = RefreshAsync();
        };

        // Progress arrives from whichever thread finished a job, and throttled, because the page
        // is not what the run is for.
        _runner.Progressed += (_, _) =>
        {
            if (DateTime.UtcNow - _lastRefresh < RefreshInterval)
            {
                return;
            }

            _lastRefresh = DateTime.UtcNow;

            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        };
    }

    public override string Title => "AI activity";

    public override string Glyph => "◷";

    public override int Position => 80;

    public override bool IsAvailable => _state.Current.Enabled;

    /// <summary>Worth saying on this page, because it changes what the save contains.</summary>
    public bool RecordsPromptBodies => _state.Current.RecordPromptBodies;

    public ObservableCollection<AiStatRow> Rows { get; } = [];

    public ObservableCollection<string> Failures { get; } = [];

    [ObservableProperty]
    private string _summary = "Nothing has been asked of a model yet.";

    /// <summary>How much of the archive has been read, at the current version (§7).</summary>
    [ObservableProperty]
    private string _coverageLine = string.Empty;

    /// <summary>How far through, 0 to 1, for the bar.</summary>
    [ObservableProperty]
    private double _coverageFraction;

    /// <summary>What the queue holds — including, deliberately, what failed.</summary>
    [ObservableProperty]
    private string _queueLine = string.Empty;

    [ObservableProperty]
    private bool _isWorking;

    /// <summary>True while the page is asking before sending anything to a model (§11.2).</summary>
    [ObservableProperty]
    private bool _needsConfirmation;

    /// <summary>Scope, cost and destination, in one sentence each.</summary>
    [ObservableProperty]
    private string _confirmationText = string.Empty;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var totals = await Task.Run(_interactions.Totals).ConfigureAwait(true);
        var kinds = await Task.Run(() => _interactions.FailureKinds()).ConfigureAwait(true);
        var coverage = await Task.Run(_coverage.Summary).ConfigureAwait(true);
        var counts = await Task.Run(() => _jobs.Counts()).ConfigureAwait(true);

        Rows.Clear();

        foreach (var total in totals)
        {
            Rows.Add(new AiStatRow(
                Purpose: Describe(total.Purpose),
                Calls: Number(total.Calls),
                Failures: total.Failures == 0 ? "—" : Number(total.Failures),
                Tokens: total.TotalTokens == 0 ? "—" : Number(total.TotalTokens),
                Median: $"{Number(total.MedianDurationMs)} ms"));
        }

        Failures.Clear();

        foreach (var (kind, count) in kinds)
        {
            Failures.Add($"{kind} × {Number(count)}");
        }

        var calls = totals.Sum(t => t.Calls);
        var tokens = totals.Sum(t => t.TotalTokens);

        Summary = calls == 0
            ? "Nothing has been asked of a model yet."
            : $"{Number(calls)} call(s), {Number(tokens)} token(s).";

        // Segmentation first, then reading: the bar follows whichever stage is under way, because
        // "every conversation is split into sessions" is not the same as "every session is read".
        CoverageFraction = coverage.IsSegmented ? coverage.ExtractedFraction : coverage.SegmentedFraction;

        CoverageLine = coverage.Threads == 0
            ? "There is nothing in this archive yet."
            : $"{Number(coverage.ThreadsSegmented)} of {Number(coverage.Threads)} conversation(s) "
              + $"split into {Number(coverage.Sessions)} session(s); "
              + $"{Number(coverage.Extracted)} of the {Number(coverage.Substantive)} worth reading have been read.";

        QueueLine = counts.Total == 0
            ? "Nothing queued."
            : $"{Number(counts.Pending)} waiting, {Number(counts.Running)} running, "
              + $"{Number(counts.Done)} done"
              + (counts.NeedsReview == 0 ? string.Empty : $", {Number(counts.NeedsReview)} to review")
              + (counts.Failed == 0 ? "." : $", {Number(counts.Failed)} failed.");

        IsWorking = _runner.IsRunning;

        // Asked where it can be answered, without having to know to press anything. Work already
        // queued — by an import, or carried in a save — waits for a yes rather than being sent,
        // and this page is where the yes is given.
        if (!NeedsConfirmation && !_declined)
        {
            var settings = _state.Current;

            if (settings.IsUsable && !AiConsent.CoversExtraction(settings, _factory))
            {
                var pending = await Task.Run(_work.PendingExtraction).ConfigureAwait(true);

                if (pending > 0)
                {
                    ConfirmationText = Describe(settings, pending);
                    NeedsConfirmation = true;
                }
            }
        }
    });

    /// <summary>
    /// Looks for work and starts on it — asking first before anything goes to a model.
    /// </summary>
    /// <remarks>
    /// Not "index the archive": the button asks what is out of date and lets the runner drain it,
    /// which is the same thing that happens after an import or on the next start. Segmentation
    /// starts at once, since it is local and free. Reading sessions with a model waits for a yes,
    /// the first time for each endpoint, with the scope, the cost and the destination in front of
    /// the person saying it (ai-plan.md §11.2).
    /// </remarks>
    [RelayCommand]
    private Task Start() => RunAsync(async () =>
    {
        // Pressing it is asking again, whatever was said earlier.
        _declined = false;

        await Task.Run(_work.PlanSegmentation).ConfigureAwait(true);

        var settings = _state.Current;
        var pending = await Task.Run(_work.PendingExtraction).ConfigureAwait(true);

        if (pending > 0 && settings.IsUsable && !AiConsent.CoversExtraction(settings, _factory))
        {
            ConfirmationText = Describe(settings, pending);
            NeedsConfirmation = true;
        }
        else if (settings.IsUsable)
        {
            await Task.Run(_work.PlanExtraction).ConfigureAwait(true);
        }

        _runner.Start();

        await RefreshAsync().ConfigureAwait(true);
    });

    /// <summary>The user said yes: remember it for this endpoint, and start reading.</summary>
    [RelayCommand]
    private Task Confirm() => RunAsync(async () =>
    {
        var settings = _state.Current.Clone();
        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, _factory);

        _state.Update(settings);

        NeedsConfirmation = false;

        await Task.Run(_work.PlanExtraction).ConfigureAwait(true);

        _runner.Start();

        await RefreshAsync().ConfigureAwait(true);
    });

    /// <summary>Not now. Segmentation carries on; nothing is sent anywhere.</summary>
    [RelayCommand]
    private void Decline()
    {
        NeedsConfirmation = false;
        _declined = true;
    }

    [RelayCommand]
    private Task Pause() => RunAsync(async () =>
    {
        await _runner.PauseAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    });

    [RelayCommand]
    private Task Clear() => RunAsync(async () =>
    {
        await Task.Run(_interactions.Clear).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    });

    /// <summary>
    /// The confirmation: how much, roughly what it costs, and where it goes.
    /// </summary>
    /// <remarks>
    /// The estimate comes from what extraction has actually cost on this archive with this model,
    /// once there is enough of it to average — never from a price list for an endpoint the user
    /// chose. And the destination sentence is there unless the endpoint is this very machine: an
    /// address on the local network is still somebody's other computer.
    /// </remarks>
    private string Describe(AiSettings settings, int sessions)
    {
        var model = settings.ModelFor(AiWorkKind.Utility);
        var extract = _interactions.Totals().FirstOrDefault(t => t.Purpose == AiPurpose.Extract);

        var cost = extract is { Calls: >= CallsBeforeEstimating } measured
            ? $"roughly {Number(measured.TotalTokens / measured.Calls * sessions)} tokens, going by the first {Number(measured.Calls)} calls"
            : "token use is unknown until the first few dozen are done";

        var destination = AiConsent.IsLocal(settings, _factory)
            ? "Everything stays on this machine."
            : $"Their text will be sent to {new Uri(AiConsent.Destination(settings, _factory)).Host}.";

        return $"This will read {Number(sessions)} conversation(s) with {model} — {cost}. {destination} "
            + "You will not be asked again for this endpoint; new conversations are read as they arrive.";
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Describe(AiPurpose purpose) => purpose switch
    {
        AiPurpose.Extract => "Extraction",
        AiPurpose.Adjudicate => "Fact merging",
        AiPurpose.Rollup => "Rollups",
        AiPurpose.Diary => "Diary",
        AiPurpose.Embed => "Embeddings",
        AiPurpose.ListModels => "Model list",
        _ => "Connection test",
    };
}
