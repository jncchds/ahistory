using System;
using Archive.Ui.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Archive.Ui.Views;

/// <summary>
/// The window shown in place of the archive when a save was made by an older version.
/// </summary>
/// <remarks>
/// It owns the transition to the real window: until the save is upgraded there is nothing behind
/// this to show, and afterwards there is no reason to keep it.
/// </remarks>
public sealed partial class UpgradeWindow : Window
{
    private Func<Window>? _openArchive;

    public UpgradeWindow() => InitializeComponent();

    /// <summary>
    /// Supplies how to open the archive once the save is usable.
    /// </summary>
    /// <remarks>
    /// A factory rather than a window: building the main window resolves view models that query
    /// the save, which must not happen until the migrations have run.
    /// </remarks>
    public void ContinueWith(Func<Window> openArchive)
    {
        _openArchive = openArchive;

        if (DataContext is UpgradeViewModel viewModel)
        {
            viewModel.Completed += OnCompleted;
        }
    }

    private void OnCompleted(object? sender, EventArgs e)
    {
        if (_openArchive is null)
        {
            return;
        }

        // Shown before this one closes: closing the last window ends the application, and for a
        // moment during the swap this is the last window.
        _openArchive().Show();

        Close();
    }

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();
}
