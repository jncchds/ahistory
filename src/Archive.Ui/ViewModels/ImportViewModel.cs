using System.Collections.ObjectModel;
using Archive.Import;
using Archive.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

/// <summary>One answer to "which source does this export belong to?".</summary>
/// <param name="IsNew">True for the option that creates a source rather than extending one.</param>
public sealed record SourceChoice(string Id, string Display, bool IsNew, bool IsSuggested);

public sealed partial class ImportViewModel(
    ImportRunner runner, IFolderPicker folderPicker, ILogger<ImportViewModel>? logger = null)
    : ViewModelBase(logger)
{
    public override string Title => "Import";

    /// <summary>Raised after a successful import so the other pages reload.</summary>
    public event Func<Task>? Imported;

    [ObservableProperty]
    private string? _exportFolder;

    [ObservableProperty]
    private ImportPreview? _preview;

    [ObservableProperty]
    private SourceChoice? _selectedSource;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private long _messagesSeen;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private ImportStats? _result;

    public ObservableCollection<SourceChoice> SourceChoices { get; } = [];

    /// <summary>The export names an account that is not yet the owner's (P5).</summary>
    public bool ShowsAccountWarning => Preview?.AccountIsNewToOwner == true;

    public string AccountWarning =>
        Preview is null || !Preview.AccountIsNewToOwner
            ? string.Empty
            : $"This export belongs to {Preview.DetectedAccountName ?? Preview.DetectedAccountId}, which is not "
              + $"yet one of {Preview.OwnerName}'s accounts. It will be treated as another of their accounts. "
              + "If this is someone else's archive, import it into a separate save instead.";

    public bool CanImport => ExportFolder is not null && Preview is not null && !IsImporting;

    [RelayCommand]
    private async Task Browse()
    {
        var folder = await folderPicker.PickAsync("Choose a Telegram export folder").ConfigureAwait(true);

        if (folder is not null)
        {
            ExportFolder = folder;
            await LoadPreviewAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Inspects the folder without writing anything, so the user can be asked where it belongs.
    /// </summary>
    public Task LoadPreviewAsync() => RunAsync(async () =>
    {
        Result = null;
        Preview = null;
        SourceChoices.Clear();

        var folder = ExportFolder;

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var preview = await Task.Run(() => runner.Preview(folder)).ConfigureAwait(true);
        Preview = preview;

        foreach (var source in preview.ExistingSources)
        {
            SourceChoices.Add(new SourceChoice(
                source.Id,
                $"{source.DisplayName} — {source.MessageCount:N0} messages",
                IsNew: false,
                source.IsSuggested));
        }

        if (!preview.SuggestedSourceExists)
        {
            SourceChoices.Add(new SourceChoice(
                preview.SuggestedSourceId,
                $"New source — {preview.SuggestedLabel ?? preview.SuggestedSourceId}",
                IsNew: true,
                IsSuggested: true));
        }

        SelectedSource = SourceChoices.FirstOrDefault(c => c.IsSuggested) ?? SourceChoices.FirstOrDefault();

        OnPropertyChanged(nameof(ShowsAccountWarning));
        OnPropertyChanged(nameof(AccountWarning));
        OnPropertyChanged(nameof(CanImport));
    });

    [RelayCommand]
    private async Task Import()
    {
        var folder = ExportFolder;
        var sourceId = SelectedSource?.Id;

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        IsImporting = true;
        Error = null;
        Result = null;
        MessagesSeen = 0;
        OnPropertyChanged(nameof(CanImport));

        try
        {
            // The whole import runs off the UI thread. It holds a write transaction per batch and
            // takes minutes on a real archive; the rest of the app stays readable throughout
            // because WAL does not block readers (decisions.md D12).
            var stats = await Task.Run(() => runner.Run(
                folder,
                progress => Report(progress),
                sourceId: sourceId)).ConfigureAwait(true);

            Result = stats;
            ProgressText = string.Empty;

            if (Imported is { } handler)
            {
                await handler().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // This path does not go through RunAsync, so it needs its own record. An import is
            // the longest and most failure-prone thing the app does; losing the reason is the
            // difference between a fixable bug and "it didn't work".
            Log.LogError(ex, "Import from the desktop app failed.");

            Error = ex.Message;
        }
        finally
        {
            IsImporting = false;
            OnPropertyChanged(nameof(CanImport));
        }
    }

    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

    /// <summary>
    /// Publishes progress, but not on every message.
    /// </summary>
    /// <remarks>
    /// The callback fires per message. At import speed, marshalling every one to the UI thread
    /// costs more than the import does — so it is throttled here, on the background thread,
    /// before anything is posted.
    /// </remarks>
    private void Report(ImportProgress progress)
    {
        var now = DateTimeOffset.UtcNow;

        if ((now - _lastReport).TotalMilliseconds < 100)
        {
            return;
        }

        _lastReport = now;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            MessagesSeen = progress.MessagesSeen;
            ProgressText = progress.CurrentChat;
        });
    }
}
