using CommunityToolkit.Mvvm.ComponentModel;

namespace Archive.Ui.ViewModels;

/// <summary>Base for every page view model.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>What the navigation sidebar calls this page.</summary>
    public abstract string Title { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    /// <summary>
    /// Loads or reloads the page's data.
    /// </summary>
    /// <remarks>
    /// Called on navigation and after an import, so a page never shows numbers from before the
    /// archive changed underneath it.
    /// </remarks>
    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs work off the UI thread and reports failure rather than tearing the app down.
    /// </summary>
    /// <remarks>
    /// Every query in this app touches SQLite, and SQLite is not something to call from the UI
    /// thread — on a real archive a query is milliseconds, but a query behind a busy writer can
    /// wait, and a frozen window is indistinguishable from a crashed one.
    /// </remarks>
    /// <returns>
    /// True when the work completed. Callers that follow an action with a reload must check it:
    /// a reload starts by clearing <see cref="Error"/>, so refreshing unconditionally after a
    /// failure wipes the explanation before anyone reads it.
    /// </returns>
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        IsBusy = true;
        Error = null;

        try
        {
            await work().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
