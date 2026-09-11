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

    public override string Glyph => "⇩";

    public override int Position => 20;

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

    /// <summary>
    /// Which account on this platform is the user's, for the formats that will not say.
    /// </summary>
    /// <remarks>
    /// Pre-filled with the importer's best guess and left editable. The alternative — which is
    /// what the app used to do — is to invent an owner: your real account then arrives as an
    /// ordinary contact, and a history file for your own account becomes a conversation between
    /// the placeholder and you that nothing downstream can tell from a real one.
    /// </remarks>
    [ObservableProperty]
    private string? _ownerAccountId;

    public ObservableCollection<SourceChoice> SourceChoices { get; } = [];

    /// <summary>Accounts the export mentions that could be the user's.</summary>
    public ObservableCollection<string> AccountCandidates { get; } = [];

    /// <summary>Whether to ask who the user is on this platform.</summary>
    public bool AsksForAccount => Preview?.DetectedAccountIsGuess == true;

    public string AccountQuestion =>
        Preview is null
            ? string.Empty
            : $"{Preview.PlatformName} archives do not say which account they belong to. "
              + "Tell it which one is yours and your own messages are attributed to you — leave it "
              + "blank and they go to a placeholder you can attribute later.";

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
        var folder = await folderPicker.PickAsync("Choose an export folder").ConfigureAwait(true);

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
        AccountCandidates.Clear();
        OwnerAccountId = null;

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

        foreach (var candidate in preview.AccountCandidates)
        {
            AccountCandidates.Add(candidate);
        }

        // The importer's own guess, offered rather than applied — it comes from a folder name.
        if (preview.DetectedAccountIsGuess)
        {
            OwnerAccountId = preview.DetectedAccountId;
        }

        OnPropertyChanged(nameof(ShowsAccountWarning));
        OnPropertyChanged(nameof(AccountWarning));
        OnPropertyChanged(nameof(AsksForAccount));
        OnPropertyChanged(nameof(AccountQuestion));
        OnPropertyChanged(nameof(CanImport));
    });

    [RelayCommand]
    private async Task Import()
    {
        var folder = ExportFolder;
        var sourceId = SelectedSource?.Id;
        var ownerAccount = string.IsNullOrWhiteSpace(OwnerAccountId) ? null : OwnerAccountId.Trim();

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
                sourceId: sourceId,
                ownerAccountId: ownerAccount)).ConfigureAwait(true);

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
