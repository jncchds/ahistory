using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Archive.Ui.ViewModels;

public sealed partial class OverviewViewModel(ArchiveQueries queries, ILogger<OverviewViewModel>? logger = null)
    : ViewModelBase(logger)
{
    public override string Title => "Overview";

    [ObservableProperty]
    private SaveSummary? _summary;

    [ObservableProperty]
    private bool _isEmpty = true;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var summary = await Task.Run(queries.Summary).ConfigureAwait(true);

        Summary = summary;
        IsEmpty = summary.Messages == 0;
    });
}
