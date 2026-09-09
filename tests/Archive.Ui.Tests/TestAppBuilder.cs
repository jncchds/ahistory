using Archive.Ui;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Archive.Ui.Tests.TestAppBuilder))]

namespace Archive.Ui.Tests;

/// <summary>
/// Boots the real <see cref="App"/> against Avalonia's headless platform.
/// </summary>
/// <remarks>
/// The same Application class the desktop head uses, so these tests exercise the real styles and
/// data templates rather than a stand-in. Headless means they run in CI on any of the three
/// target platforms with no display attached.
/// </remarks>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
