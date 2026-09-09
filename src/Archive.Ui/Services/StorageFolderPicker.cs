using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Archive.Ui.Services;

/// <summary>The platform's own folder dialog, via Avalonia's storage provider.</summary>
public sealed class StorageFolderPicker : IFolderPicker
{
    public async Task<string?> PickAsync(string title)
    {
        var top = TopLevel();

        if (top?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);

        // TryGetLocalPath returns null for locations with no filesystem path — a cloud provider,
        // say. The importer needs real files, so that is a non-answer rather than a folder.
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static TopLevel? TopLevel() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
