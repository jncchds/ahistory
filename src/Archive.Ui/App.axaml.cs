using Archive.Ui.ViewModels;
using Archive.Ui.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Ui;

public sealed class App : Application
{
    /// <summary>
    /// The container the desktop head built.
    /// </summary>
    /// <remarks>
    /// Static because Avalonia constructs the Application itself and offers no way to pass
    /// anything in. The head sets it before <c>Start</c>, so it is assigned exactly once, before
    /// any view model exists.
    /// </remarks>
    public static IServiceProvider? Services { get; set; }

    /// <summary>
    /// Set by the head when the save is behind, and the archive cannot be opened until it is
    /// carried forward.
    /// </summary>
    /// <remarks>
    /// Static for the same reason as <see cref="Services"/>: Avalonia constructs the Application
    /// and there is nowhere to pass anything in.
    /// </remarks>
    public static UpgradeViewModel? PendingUpgrade { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Services
                ?? throw new InvalidOperationException(
                    "App.Services was not set. The desktop head must build the container before starting Avalonia.");

            // Nothing in the archive can be read until its schema is the one the queries were
            // written against, so the question comes first and instead — not over the top of a
            // window bound to tables that are not there yet.
            if (PendingUpgrade is { } upgrade)
            {
                var prompt = new UpgradeWindow { DataContext = upgrade };
                prompt.ContinueWith(() => ArchiveWindow(services));

                desktop.MainWindow = prompt;
            }
            else
            {
                desktop.MainWindow = ArchiveWindow(services);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindow ArchiveWindow(IServiceProvider services) =>
        new() { DataContext = services.GetRequiredService<MainWindowViewModel>() };
}
