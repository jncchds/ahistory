using Archive.Cli;
using Archive.Logging;
using Serilog.Events;

// Headless entry point. This exists so the V1 acceptance criterion — a real export re-imports
// as a no-op — is provable without a UI, and so each storage milestone is demonstrable before
// Archive.Desktop exists. Commands arrive with their milestones: `hash` in M2, `import` in M3.

// The CLI writes to both, at different levels. The file keeps the full record — an import is
// the thing you most want to look back at tomorrow. The console stays at Warning, because the
// commands already print what they did; the console log is for what they did not expect.
var verbose = args.Contains("--verbose");

using var loggerFactory = ArchiveLog.Create(new LogSettings(
    LogSettings.DefaultDirectory,
    MinimumLevel: verbose ? LogEventLevel.Debug : LogEventLevel.Information,
    ToConsole: true,
    ConsoleMinimumLevel: verbose ? LogEventLevel.Debug : LogEventLevel.Warning));

return Commands.Run(args, loggerFactory);
