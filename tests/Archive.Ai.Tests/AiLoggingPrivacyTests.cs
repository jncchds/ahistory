using System.Net;
using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Sessions;
using Microsoft.Extensions.Logging;

namespace Archive.Ai.Tests;

/// <summary>
/// AGENTS.md P6, for the part of the app most tempted to break it.
/// </summary>
/// <remarks>
/// The AI layer sends people's correspondence to an endpoint and gets text back. It is the easiest
/// place in the codebase to log a prompt, an argument or an error body "for debugging", and each of
/// those puts private words into a file people attach to bug reports. This runs extraction against
/// an endpoint that fails and one that misbehaves, and fails if any word anyone wrote — or anything
/// the endpoint said back — reaches the log.
/// </remarks>
public sealed class AiLoggingPrivacyTests : IDisposable
{
    private const long Noon = 1_700_000_000;
    private const long Hour = 3_600;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Keeps every line, rendered as a sink would render it, exception included.</summary>
    private sealed class Capture<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add($"{formatter(state, exception)} {exception}");
            }
        }
    }

    [Fact]
    public async Task Running_extraction_puts_nothing_anyone_wrote_into_the_log()
    {
        using var save = new TempSave();

        save.Seed(
            "t1",
            (Noon, "Zanzibar is where the treasure is buried"),
            (Noon + (9 * Hour), "Zanzibar again, a second conversation entirely"));

        new SessionSegmenter(save.Database).SegmentThread("t1");
        save.Execute("UPDATE session SET is_substantive = 1;");

        var lines = new List<string>();

        // One call refused with an error body that quotes its request, then prose where a tool
        // call belonged: the two answers most likely to be logged by someone trying to help.
        var endpoint = new FakeEndpoint()
            .Answers("""{"error":{"message":"Quixotic: the request said Zanzibar"}}""", HttpStatusCode.BadRequest)
            .Answers("""{"choices":[{"message":{"content":"Marmalade, and the treasure"},"finish_reason":"stop"}]}""");

        var factory = new LlmProviderFactory(endpoint);

        var settings = new AiSettings
        {
            Enabled = true,
            Endpoint = "http://localhost:1234/v1",
            MainModel = "test-model",
            MaxRetries = 0,
            RetryBaseDelayMs = 1,
            MaxParallelCalls = 1,
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        };

        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, factory);

        var store = new AiSettingsStore(_directory, new Capture<AiSettingsStore>(lines));
        store.Save(settings);

        var state = new AiState(store);
        var jobs = new AiJobs(save.Database);
        var client = new AiClient(factory, save.Interactions, new Capture<AiClient>(lines));

        var extractor = new ExtractRunner(
            client,
            new ExtractionWindows(save.Database),
            new FactWriter(save.Database),
            new Capture<ExtractRunner>(lines));

        using var runner = new AiRunner(
            jobs, state, [new ExtractJobHandler(extractor, state, factory)], new Capture<AiRunner>(lines));

        new AiWork(save.Database, jobs, new SessionSegmenter(save.Database), runner).PlanExtraction();

        await runner.DrainAsync();

        var log = string.Join('\n', lines);

        // Something was logged, including the failure — so this is not passing by saying nothing.
        Assert.Contains("failed", log, StringComparison.Ordinal);
        Assert.Contains("no tool calls", log, StringComparison.Ordinal);

        foreach (var word in new[] { "Zanzibar", "treasure", "buried", "Quixotic", "Marmalade" })
        {
            Assert.DoesNotContain(word, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
