using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Media;
using Archive.Ui;
using Archive.Ui.Services;
using Archive.Ui.ViewModels;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;

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
        try
        {
            var options = ResolveOptions(args);

            // Migrating before the window opens means a schema problem is a message on a console
            // rather than a half-drawn window bound to tables that do not exist.
            var database = new Database(options);
            database.Migrate();
            Directory.CreateDirectory(options.ResolveMediaDirectory());

            App.Services = BuildContainer(options, database);

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ahistory failed to start: {ex.Message}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static ServiceProvider BuildContainer(ArchiveOptions options, Database database)
    {
        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton(database);
        services.AddSingleton<IMediaStore>(_ => new FileSystemMediaStore(options));
        services.AddSingleton<ArchiveQueries>();
        services.AddSingleton<IdentityMerger>();
        services.AddSingleton<PersonConversation>();
        services.AddSingleton<ImportRunner>();
        services.AddSingleton<IFolderPicker, StorageFolderPicker>();

        // Pages are singletons: they hold the reader's place — which conversation is open, how
        // far back they have scrolled — and navigating away and back should not discard it.
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<PersonViewModel>();
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
