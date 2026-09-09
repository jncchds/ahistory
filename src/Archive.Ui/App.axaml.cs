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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Services
                ?? throw new InvalidOperationException(
                    "App.Services was not set. The desktop head must build the container before starting Avalonia.");

            desktop.MainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
