using Archive.Ui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Archive.Ui.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Loads the first page's data once the window exists.
    /// </summary>
    /// <remarks>
    /// Not in the view model's constructor: construction happens during DI resolution, and a
    /// database query there would run before there is a window to show a failure in.
    /// </remarks>
    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.LoadedCommand.ExecuteAsync(null);
        }
    }
}
