using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Archive.Logging;

/// <summary>
/// Where log files live and how long they are kept.
/// </summary>
/// <param name="Directory">Folder for log files.</param>
/// <param name="MinimumLevel">
/// What reaches the file. This is the record that still exists tomorrow, so it is not the place
/// to be frugal.
/// </param>
/// <param name="ToConsole">Whether to also write to the console. The CLI does; the desktop app does not.</param>
/// <param name="ConsoleMinimumLevel">
/// What reaches the console, which is a different question. The CLI's commands already print what
/// they did; the console log is for what they did not expect, so it starts at Warning while the
/// file keeps everything.
/// </param>
/// <param name="RetainedDays">How many days of logs to keep.</param>
public sealed record LogSettings(
    string Directory,
    LogEventLevel MinimumLevel = LogEventLevel.Information,
    bool ToConsole = false,
    LogEventLevel ConsoleMinimumLevel = LogEventLevel.Warning,
    int RetainedDays = 14)
{
    /// <summary>
    /// The default location: under the user's application data, never beside the save.
    /// </summary>
    /// <remarks>
    /// Deliberately not next to the <c>.db</c>. A save is meant to be copied and moved around as
    /// a unit (P6), and logs riding along with it is exactly how a diagnostic file ends up
    /// somewhere nobody intended.
    /// </remarks>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ahistory",
        "logs");
}

/// <summary>
/// Builds the logger the whole app writes through.
/// </summary>
/// <remarks>
/// <para>
/// <b>What may be logged.</b> This archive is people's private correspondence, and a log that
/// quotes it is as sensitive as the archive itself — while being far more likely to be attached
/// to a bug report. So the rule is: <b>counts, identifiers, durations and error types. Never
/// content, never names.</b>
/// </para>
/// <para>
/// Concretely: no message text, no chat or contact display names, no entity JSON, no raw export
/// JSON, no search terms. Refer to things by their ids — <c>telegram:100</c> says everything a
/// diagnostic needs and nothing a stranger could read. Enforced by
/// <c>LoggingPrivacyTests</c> against a real import.
/// </para>
/// <para>
/// The one deliberate exception is the export folder path, logged once when an import starts: it
/// is what the user chose themselves, and an import that cannot say where it read from is very
/// hard to diagnose.
/// </para>
/// </remarks>
public static class ArchiveLog
{
    /// <summary>Creates a logger factory writing to a rolling daily file, and optionally the console.</summary>
    public static ILoggerFactory Create(LogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        System.IO.Directory.CreateDirectory(settings.Directory);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(settings.MinimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(settings.Directory, "ahistory-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: settings.RetainedDays,
                // A runaway loop must not fill the disk of a machine whose whole point is
                // holding an archive.
                fileSizeLimitBytes: 32 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {SourceContext} {Message:lj}{NewLine}{Exception}");

        if (settings.ToConsole)
        {
            configuration = configuration.WriteTo.Console(
                restrictedToMinimumLevel: settings.ConsoleMinimumLevel,
                standardErrorFromLevel: LogEventLevel.Warning,
                outputTemplate: "{Level:u3} {Message:lj}{NewLine}{Exception}");
        }

        return new SerilogLoggerFactory(configuration.CreateLogger(), dispose: true);
    }

    /// <summary>
    /// Records failures nobody caught.
    /// </summary>
    /// <remarks>
    /// A desktop app has no console anyone reads, so without this an unhandled exception is a
    /// window that vanishes and a user with nothing to report. Unobserved task exceptions are
    /// marked observed so a background failure logs rather than tearing the process down.
    /// </remarks>
    public static void CatchUnhandled(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; the process is terminating.");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "A background task failed and nobody was waiting on it.");
            e.SetObserved();
        };
    }
}
