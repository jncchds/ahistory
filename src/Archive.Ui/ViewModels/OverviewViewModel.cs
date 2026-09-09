using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

public sealed partial class OverviewViewModel(ArchiveQueries queries, ILogger<OverviewViewModel>? logger = null)
    : ViewModelBase(logger)
{
    public override string Title => "Overview";

    public override string Glyph => "◱";

    [ObservableProperty]
    private SaveSummary? _summary;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>The stored ISO timestamp is for sorting, not for reading.</summary>
    public string LastImport =>
        Summary?.LastImportUtc is { } utc && DateTimeOffset.TryParse(utc, out var at)
            ? at.LocalDateTime.ToString("d MMM yyyy, HH:mm")
            : "never";

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var summary = await Task.Run(queries.Summary).ConfigureAwait(true);

        Summary = summary;
        IsEmpty = summary.Messages == 0;
        OnPropertyChanged(nameof(LastImport));
    });
}
