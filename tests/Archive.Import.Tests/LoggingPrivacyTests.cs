using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Archive.Import.Tests;

/// <summary>
/// A log for this app must be safe to attach to a bug report.
/// </summary>
/// <remarks>
/// The archive is people's private correspondence. A log that quotes it is as sensitive as the
/// archive itself, while being far more likely to be copied somewhere else — so the rule is
/// counts, identifiers, durations and error types; never content, never names.
///
/// This runs a real import through a capturing logger and looks for the fixture's own words. It
/// is worth more than the rule written down anywhere, because the failure mode is someone adding
/// one helpful-looking `{ChatName}` to a message six months from now.
/// </remarks>
public sealed class LoggingPrivacyTests
{
    /// <summary>Distinctive strings from the fixture that must never reach a log.</summary>
    private static readonly string[] Forbidden =
    [
        // Message text.
        "the harbour was freezing",
        "we should go back",
        "yeah exactly",
        "see you thursday",
        // Chat and contact names — who someone talks to is as revealing as what they said.
        "Sam Ruiz",
        "Prague trip",
        "Old book club",
    ];

    [Fact]
    public void An_import_logs_nothing_from_inside_the_export()
    {
        using var save = new TempSave();
        var capture = new CapturingLoggerProvider();

        using (var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capture);
            // Debug, so the per-chat and per-attachment lines are included: the quiet levels are
            // exactly where an incautious message would hide.
            builder.SetMinimumLevel(LogLevel.Debug);
        }))
        {
            var runner = new ImportRunner(
                save.Database, save.MediaStore, factory.CreateLogger<ImportRunner>());

            runner.Run(Fixtures.Directory("group-and-dm"));
        }

        var log = capture.Text;

        Assert.NotEmpty(log);

        foreach (var secret in Forbidden)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The log still has to be useful, or the privacy rule has simply produced a silent app.
    /// </summary>
    [Fact]
    public void An_import_logs_enough_to_diagnose_it()
    {
        using var save = new TempSave();
        var capture = new CapturingLoggerProvider();

        using (var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capture);
            builder.SetMinimumLevel(LogLevel.Debug);
        }))
        {
            var runner = new ImportRunner(
                save.Database, save.MediaStore, factory.CreateLogger<ImportRunner>());

            runner.Run(Fixtures.Directory("group-and-dm"));
        }

        var log = capture.Text;

        Assert.Contains("starting", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finished", log, StringComparison.OrdinalIgnoreCase);

        // Threads by id, which is what a diagnostic needs and a stranger cannot read.
        Assert.Contains("100", log, StringComparison.Ordinal);
        Assert.Contains("telegram:account:777001", log, StringComparison.Ordinal);
    }

    /// <summary>A failure has to say what went wrong without quoting what it was reading.</summary>
    [Fact]
    public void A_failed_import_logs_the_error_without_the_content()
    {
        using var save = new TempSave();
        var capture = new CapturingLoggerProvider();

        using (var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capture);
            builder.SetMinimumLevel(LogLevel.Debug);
        }))
        {
            var runner = new ImportRunner(
                save.Database, save.MediaStore, factory.CreateLogger<ImportRunner>());

            Assert.ThrowsAny<Exception>(() => runner.Run(Fixtures.Directory("unknown-prefix")));
        }

        var log = capture.Text;

        Assert.Contains("failed", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spaceship42", log, StringComparison.Ordinal);

        // The message that carried the unknown prefix must not have come along with it.
        Assert.DoesNotContain(
            "from a participant kind that does not exist yet", log, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collects everything written, message and exception alike.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        internal string Text => string.Join("\n", _lines);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception));

                if (exception is not null)
                {
                    lines.Enqueue(exception.ToString());
                }
            }
        }
    }
}
