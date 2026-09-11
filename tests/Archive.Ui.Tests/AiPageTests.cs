using Archive.Ai;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Sessions;
using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

/// <summary>
/// The behavioural half of AGENTS.md P1: with AI off there is nothing of it in the window, and
/// switching it takes effect where the user can see it.
/// </summary>
public sealed class AiPageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private AiState State() => new(new AiSettingsStore(_directory));

    private static AiSettings Usable() => new()
    {
        Enabled = true,
        MainModel = "test-model",
        Endpoint = "http://localhost:1234/v1",
        DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        MaxRetries = 0,
        RetryBaseDelayMs = 1,
    };

    /// <summary>The activity page, wired to a queue and a runner that have nothing to do.</summary>
    private static AiStatsViewModel Stats(TempSave save, AiState state)
    {
        var jobs = new AiJobs(save.Database);
        var segmenter = new SessionSegmenter(save.Database);
        var runner = new AiRunner(jobs, state, []);

        return new AiStatsViewModel(
            state,
            new AiInteractions(save.Database),
            new AiCoverage(save.Database),
            jobs,
            runner,
            new AiWork(save.Database, jobs, segmenter, runner),
            new LlmProviderFactory());
    }

    private static AiClient Client(TempSave save, params string[] answers) =>
        new(new LlmProviderFactory(new Canned(answers)), new AiInteractions(save.Database));

    /// <summary>
    /// Answers in order, then repeats the last one.
    /// </summary>
    /// <remarks>
    /// These tests are about what a page does with an answer, so the endpoint only has to produce
    /// one. What the provider puts on the wire is checked in Archive.Ai.Tests, against a handler
    /// that keeps the requests.
    /// </remarks>
    private sealed class Canned(string[] answers) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = answers.Length == 0
                ? "{}"
                : answers[Math.Min(_next++, answers.Length - 1)];

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Prose = """
        {"choices":[{"message":{"content":"ready"},"finish_reason":"stop"}]}
        """;

    private const string ToolCall = """
        {"choices":[{"message":{"content":null,"tool_calls":[
            {"id":"c1","type":"function","function":{"name":"report_ready","arguments":"{}"}}]},
          "finish_reason":"tool_calls"}]}
        """;

    /// <summary>
    /// A page that reports facts about a feature nobody enabled is the empty panel P1 refuses.
    /// </summary>
    [Fact]
    public void An_ai_page_is_out_of_the_rail_until_ai_is_switched_on()
    {
        using var save = new TempSave();

        var state = State();
        var stats = Stats(save, state);

        var window = new MainWindowViewModel(save.Options, [new OverviewViewModel(save.Queries), stats]);

        Assert.Equal(["Overview"], window.Pages.Select(p => p.Title));

        state.Update(Usable());

        Assert.Equal(["Overview", "AI activity"], window.Pages.Select(p => p.Title));
    }

    /// <summary>
    /// Switching it off again leaves the window exactly as it was.
    /// </summary>
    /// <remarks>
    /// And leaves the reader where they were: a page appearing or disappearing must not be able to
    /// throw someone out of the conversation they were reading.
    /// </remarks>
    [Fact]
    public void Switching_ai_off_removes_its_pages_and_does_not_move_the_reader()
    {
        using var save = new TempSave();

        var state = State();
        state.Update(Usable());

        var overview = new OverviewViewModel(save.Queries);
        var stats = Stats(save, state);
        var window = new MainWindowViewModel(save.Options, [overview, stats])
        {
            CurrentPage = overview,
        };

        var off = Usable();
        off.Enabled = false;
        state.Update(off);

        Assert.Equal(["Overview"], window.Pages.Select(p => p.Title));
        Assert.Same(overview, window.CurrentPage);
    }

    /// <summary>The page being read disappearing is the one case where the reader has to move.</summary>
    [Fact]
    public void The_reader_is_moved_only_when_the_page_they_are_on_goes_away()
    {
        using var save = new TempSave();

        var state = State();
        state.Update(Usable());

        var overview = new OverviewViewModel(save.Queries);
        var stats = Stats(save, state);
        var window = new MainWindowViewModel(save.Options, [overview, stats])
        {
            CurrentPage = stats,
        };

        var off = Usable();
        off.Enabled = false;
        state.Update(off);

        Assert.Same(overview, window.CurrentPage);
    }

    /// <summary>
    /// The settings page is the exception, because it is where the switch is.
    /// </summary>
    [Fact]
    public void The_settings_page_is_always_reachable()
    {
        using var save = new TempSave();

        var state = State();
        var settings = new AiSettingsViewModel(state, Client(save), new AiConnectionCheck(Client(save)));

        Assert.True(settings.IsAvailable);
        Assert.False(state.Current.Enabled);
    }

    [Fact]
    public async Task Saving_records_the_disclaimer_that_was_accepted()
    {
        using var save = new TempSave();

        var state = State();
        var page = new AiSettingsViewModel(state, Client(save), new AiConnectionCheck(Client(save)))
        {
            Enabled = true,
            MainModel = "google/gemma-4-31b",
            Endpoint = "http://192.168.2.33:1234/v1",
            DisclaimerAccepted = true,
        };

        await page.SaveCommand.ExecuteAsync(null);
        await page.Pending;

        Assert.True(state.Current.IsUsable);
        Assert.Equal(AiSettings.CurrentDisclaimerVersion, state.Current.DisclaimerAcknowledgedVersion);

        // And it survives the app being closed, because that is what the file is for.
        Assert.Equal("google/gemma-4-31b", new AiSettingsStore(_directory).Load().MainModel);
    }

    /// <summary>
    /// Switching AI on is a consent step, so it cannot be completed without consenting.
    /// </summary>
    [Fact]
    public void Ai_cannot_be_enabled_without_a_model_and_an_acknowledgement()
    {
        using var save = new TempSave();

        var page = new AiSettingsViewModel(State(), Client(save), new AiConnectionCheck(Client(save)))
        {
            Enabled = true,
        };

        Assert.False(page.CanSave);

        page.MainModel = "test-model";

        Assert.False(page.CanSave);

        page.DisclaimerAccepted = true;

        Assert.True(page.CanSave);

        // Switching it off needs neither, because there is nothing to consent to.
        page.Enabled = false;
        page.DisclaimerAccepted = false;

        Assert.True(page.CanSave);
    }

    [Fact]
    public async Task Test_connection_reports_a_model_that_cannot_call_a_tool()
    {
        using var save = new TempSave();

        var check = new AiConnectionCheck(Client(save, Prose, Prose));
        var page = new AiSettingsViewModel(State(), Client(save), check)
        {
            MainModel = "test-model",
            Endpoint = "http://localhost:1234/v1",
        };

        await page.TestConnectionCommand.ExecuteAsync(null);
        await page.Pending;

        Assert.False(page.StatusIsGood);
        Assert.Contains("tool calling", page.Status!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_connection_reports_a_usable_model()
    {
        using var save = new TempSave();

        var check = new AiConnectionCheck(Client(save, Prose, ToolCall));
        var page = new AiSettingsViewModel(State(), Client(save), check)
        {
            MainModel = "test-model",
            Endpoint = "http://localhost:1234/v1",
        };

        await page.TestConnectionCommand.ExecuteAsync(null);
        await page.Pending;

        Assert.True(page.StatusIsGood);
    }

    [Fact]
    public async Task Load_models_fills_the_suggestions()
    {
        using var save = new TempSave();

        var page = new AiSettingsViewModel(
            State(),
            Client(save, """{"data":[{"id":"gemma"},{"id":"nomic-embed"}]}"""),
            new AiConnectionCheck(Client(save)))
        {
            Endpoint = "http://localhost:1234/v1",
        };

        await page.LoadModelsCommand.ExecuteAsync(null);
        await page.Pending;

        Assert.Equal(["gemma", "nomic-embed"], page.ModelSuggestions);
    }

    /// <summary>
    /// A save with one conversation worth reading and no messages to segment.
    /// </summary>
    /// <remarks>
    /// No messages, so pressing Start plans no segmentation and the background runner has nothing
    /// local to do: the test is about the question asked before a model reads anything.
    /// </remarks>
    private static void OneUnreadSession(TempSave save) => save.Execute("""
        INSERT INTO import_source (id, platform, created_utc)
            VALUES ('src', 'telegram', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO import (id, source_id, platform, source_path, source_fingerprint,
                            importer_version, status, started_utc)
            VALUES ('imp', 'src', 'telegram', '/tmp', 'fp', '1', 'completed', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO thread (id, platform, source_thread_id, kind, first_import_id, created_utc)
            VALUES ('t1', 'telegram', 't1', 'dm', 'imp', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO session (id, thread_id, started_at_unix, ended_at_unix, message_count,
                             member_hash, segmenter_version, is_substantive, filter_version)
            VALUES ('s1', 't1', 1700000000, 1700000060, 1, 'h', '1', 1, '2');
        """);

    /// <summary>
    /// Before anything goes to a model, the page says how much, what it costs and where it goes.
    /// </summary>
    [Fact]
    public async Task Starting_asks_before_sending_anything_to_a_model()
    {
        using var save = new TempSave();
        OneUnreadSession(save);

        var state = State();
        state.Update(Usable());

        var jobs = new AiJobs(save.Database);
        var runner = new AiRunner(jobs, state, []);
        var page = new AiStatsViewModel(
            state,
            new AiInteractions(save.Database),
            new AiCoverage(save.Database),
            jobs,
            runner,
            new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner),
            new LlmProviderFactory());

        try
        {
            await page.StartCommand.ExecuteAsync(null);

            Assert.True(page.NeedsConfirmation);
            Assert.Contains("1 conversation", page.ConfirmationText, StringComparison.Ordinal);
            Assert.Contains("stays on this machine", page.ConfirmationText, StringComparison.Ordinal);

            // Nothing was queued for a model while the question stands.
            Assert.Equal(0, jobs.Counts(AiJobKind.Extract).Total);

            await page.ConfirmCommand.ExecuteAsync(null);

            Assert.False(page.NeedsConfirmation);
            Assert.Equal("http://localhost:1234/v1/", state.Current.ExtractionConfirmedFor);
            Assert.Equal(1, jobs.Counts(AiJobKind.Extract).Total);
        }
        finally
        {
            await runner.PauseAsync();
        }
    }

    /// <summary>
    /// Work waiting for a yes is asked about when the page is looked at, without pressing anything.
    /// </summary>
    /// <remarks>
    /// Found in the real app: after a launch the runner idles, the page showed Pause instead of
    /// Start, and so the one control that asked the question could not be reached. Queued work sat
    /// waiting for an answer nobody could give. And once declined, the question stays away for the
    /// session — the page refreshes on every progress tick and must not keep re-opening it.
    /// </remarks>
    [Fact]
    public async Task Work_waiting_for_a_yes_is_asked_about_without_pressing_anything()
    {
        using var save = new TempSave();
        OneUnreadSession(save);

        var state = State();
        state.Update(Usable());

        var jobs = new AiJobs(save.Database);
        var runner = new AiRunner(jobs, state, []);
        var page = new AiStatsViewModel(
            state,
            new AiInteractions(save.Database),
            new AiCoverage(save.Database),
            jobs,
            runner,
            new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner),
            new LlmProviderFactory());

        await page.RefreshAsync();

        Assert.True(page.NeedsConfirmation);

        page.DeclineCommand.Execute(null);
        await page.RefreshAsync();

        Assert.False(page.NeedsConfirmation);
        Assert.Equal(0, jobs.Counts(AiJobKind.Extract).Total);
    }

    /// <summary>Saying no leaves nothing queued, and nothing recorded as agreed.</summary>
    [Fact]
    public async Task Declining_sends_nothing_and_agrees_to_nothing()
    {
        using var save = new TempSave();
        OneUnreadSession(save);

        var state = State();
        state.Update(Usable());

        var jobs = new AiJobs(save.Database);
        var runner = new AiRunner(jobs, state, []);
        var page = new AiStatsViewModel(
            state,
            new AiInteractions(save.Database),
            new AiCoverage(save.Database),
            jobs,
            runner,
            new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner),
            new LlmProviderFactory());

        try
        {
            await page.StartCommand.ExecuteAsync(null);
            page.DeclineCommand.Execute(null);

            Assert.False(page.NeedsConfirmation);
            Assert.Equal(string.Empty, state.Current.ExtractionConfirmedFor);
            Assert.Equal(0, jobs.Counts(AiJobKind.Extract).Total);
        }
        finally
        {
            await runner.PauseAsync();
        }
    }

    /// <summary>
    /// A box on the local network is named, because it is not this machine.
    /// </summary>
    [Fact]
    public async Task The_question_names_where_text_would_go_when_it_leaves_the_machine()
    {
        using var save = new TempSave();
        OneUnreadSession(save);

        var state = State();
        var remote = Usable();
        remote.Endpoint = "http://192.168.2.33:1234/v1";
        state.Update(remote);

        var jobs = new AiJobs(save.Database);
        var runner = new AiRunner(jobs, state, []);
        var page = new AiStatsViewModel(
            state,
            new AiInteractions(save.Database),
            new AiCoverage(save.Database),
            jobs,
            runner,
            new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner),
            new LlmProviderFactory());

        try
        {
            await page.StartCommand.ExecuteAsync(null);

            Assert.Contains("192.168.2.33", page.ConfirmationText, StringComparison.Ordinal);
            Assert.DoesNotContain("stays on this machine", page.ConfirmationText, StringComparison.Ordinal);
        }
        finally
        {
            await runner.PauseAsync();
        }
    }

    /// <summary>The wait is set in seconds on the page and kept in milliseconds in the file.</summary>
    [Fact]
    public async Task The_wait_for_an_answer_is_saved_and_shown_in_seconds()
    {
        using var save = new TempSave();

        var state = State();
        var page = new AiSettingsViewModel(state, Client(save), new AiConnectionCheck(Client(save)))
        {
            TimeoutSeconds = 600,
        };

        await page.SaveCommand.ExecuteAsync(null);
        await page.Pending;

        Assert.Equal(600_000, state.Current.TimeoutMs);
        Assert.Equal(600, new AiSettingsViewModel(state, Client(save), new AiConnectionCheck(Client(save))).TimeoutSeconds);
    }

    /// <summary>
    /// Forgetting needs the word typed, and then takes everything the AI layer produced.
    /// </summary>
    [Fact]
    public async Task Forgetting_everything_waits_for_the_word_and_then_forgets()
    {
        using var save = new TempSave();
        OneUnreadSession(save);

        var state = State();
        var jobs = new AiJobs(save.Database);
        var runner = new AiRunner(jobs, state, []);
        var page = new AiSettingsViewModel(
            state, Client(save), new AiConnectionCheck(Client(save)), new AiForget(save.Database), runner);

        await page.RefreshAsync();

        Assert.True(page.CanOfferForget);
        Assert.True(page.HasSomethingToForget);
        Assert.False(page.CanForget);

        page.ForgetConfirmation = "yes";
        Assert.False(page.CanForget);

        page.ForgetConfirmation = " Forget ";
        Assert.True(page.CanForget);

        await page.ForgetEverythingCommand.ExecuteAsync(null);
        await page.Pending;

        Assert.False(page.HasSomethingToForget);
        Assert.Equal(string.Empty, page.ForgetConfirmation);
        Assert.Contains("Forgotten", page.Status, StringComparison.Ordinal);
        Assert.True(new AiForget(save.Database).Counts().IsEmpty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
