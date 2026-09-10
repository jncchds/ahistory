using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Logging;
using Archive.Media;
using Archive.Ui;
using Archive.Ui.Services;
using Archive.Ui.ViewModels;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Archive.Desktop;

/// <summary>
/// The desktop head: build the container, migrate the save, start Avalonia.
/// </summary>
/// <remarks>
/// Deliberately thin. Every view and view model lives in Archive.Ui, so the headless UI tests
/// never need an AppBuilder and a second head would be a new project rather than a refactor.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var loggerFactory = ArchiveLog.Create(new LogSettings(
            LogSettings.DefaultDirectory,
            MinimumLevel: args.Contains("--verbose") ? LogEventLevel.Debug : LogEventLevel.Information));

        var log = loggerFactory.CreateLogger("Archive.Desktop");

        // A desktop app has no console anyone is watching, so a crash without this is a window
        // that vanishes and a user with nothing to report.
        ArchiveLog.CatchUnhandled(log);

        try
        {
            var options = ResolveOptions(args);

            // Migrating before the window opens means a schema problem is a message in the log
            // rather than a half-drawn window bound to tables that do not exist.
            var database = new Database(options, loggerFactory.CreateLogger<Database>());

            // The resolved path, not the raw argument: a relative path in the log is a path
            // nobody can find again.
            log.LogInformation(
                "ahistory starting. Save {SavePath}, media {MediaPath}, logs {LogPath}.",
                database.DatabasePath, options.ResolveMediaDirectory(), LogSettings.DefaultDirectory);

            // Inspected rather than migrated outright: a save made by an older version is carried
            // forward only if someone says so, and the only way to ask is to have a window. So the
            // question is handed to the UI and the app starts either way.
            var status = database.Inspect();

            if (status.CanUpgrade)
            {
                log.LogInformation(
                    "The save is behind by {Count} migration(s); asking before upgrading.",
                    status.Pending.Count);

                App.PendingUpgrade = new UpgradeViewModel(
                    database,
                    status.Pending,
                    BackupPathFor(database.DatabasePath, status.Pending),
                    loggerFactory.CreateLogger<UpgradeViewModel>());
            }
            else
            {
                // Creates a new save, or refuses one this build cannot open, with the reason.
                database.Migrate();
            }

            Directory.CreateDirectory(options.ResolveMediaDirectory());

            App.Services = BuildContainer(options, database, loggerFactory);

            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            log.LogInformation("ahistory exiting with code {ExitCode}.", exitCode);

            return exitCode;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "ahistory failed to start.");
            Console.Error.WriteLine($"ahistory failed to start: {ex.Message}");
            Console.Error.WriteLine($"Details in {LogSettings.DefaultDirectory}");

            return 1;
        }
    }

    /// <summary>
    /// Where the copy taken before an upgrade goes: beside the save, named for the migration it
    /// predates, never overwriting an existing file.
    /// </summary>
    /// <remarks>Matches what `ahistory init --upgrade` does, so both heads leave the same trail.</remarks>
    private static string BackupPathFor(string savePath, IReadOnlyList<string> pending)
    {
        var stage = pending[0].Split('_')[0];
        var candidate = $"{savePath}.pre-{stage}";

        for (var n = 2; File.Exists(candidate); n++)
        {
            candidate = $"{savePath}.pre-{stage}-{n}";
        }

        return candidate;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static ServiceProvider BuildContainer(
        ArchiveOptions options, Database database, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();

        // The factory the head already built, so everything logs to one file with one policy.
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        services.AddSingleton(options);
        services.AddSingleton(database);
        services.AddSingleton<IMediaStore>(_ => new FileSystemMediaStore(options));
        services.AddSingleton<ArchiveQueries>();
        services.AddSingleton<IdentityMerger>();
        services.AddSingleton<MergeSuggestions>();
        services.AddSingleton<PersonConversation>();
        services.AddSingleton<ArchiveSearch>();
        services.AddSingleton<ImportRunner>();
        services.AddSingleton<IFolderPicker, StorageFolderPicker>();

        // Pages are singletons: they hold the reader's place — which conversation is open, how
        // far back they have scrolled — and navigating away and back should not discard it.
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<PersonViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<PeopleViewModel>();
        services.AddSingleton<ThreadsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Works out which save to open.
    /// </summary>
    /// <remarks>
    /// <c>--save &lt;path&gt;</c> wins, then <c>AHISTORY_Archive__DatabasePath</c>, then a default under
    /// the user's local application data. There is always a default because a desktop app that
    /// refuses to start until it is configured is a worse first experience than one that opens an
    /// empty archive and tells you to import something.
    /// </remarks>
    private static ArchiveOptions ResolveOptions(string[] args)
    {
        var index = Array.IndexOf(args, "--save");

        var path = index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Environment.GetEnvironmentVariable($"AHISTORY_{ArchiveOptions.SectionName}__{nameof(ArchiveOptions.DatabasePath)}")
              ?? Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                  "ahistory",
                  "archive.db");

        var options = new ArchiveOptions { DatabasePath = path };
        options.Validate();

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return options;
    }
}
