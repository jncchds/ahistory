using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ui.ViewModels;

/// <summary>Base for every page view model.</summary>
public abstract partial class ViewModelBase(ILogger? logger = null) : ObservableObject
{
    /// <summary>
    /// Where failures go.
    /// </summary>
    /// <remarks>
    /// Optional so tests can construct a page without ceremony, and so a missing registration
    /// degrades to silence rather than a startup crash.
    /// </remarks>
    protected ILogger Log { get; } = logger ?? NullLogger.Instance;

    /// <summary>What the navigation rail calls this page.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Where the page sits in the rail, lowest first.
    /// </summary>
    /// <remarks>
    /// The window is handed its pages by the container rather than naming them in a list, so that
    /// an optional feature can contribute one without the window learning it exists. The order has
    /// to come from somewhere, and a registration order is not somewhere — it is whatever the head
    /// happened to type.
    /// </remarks>
    public abstract int Position { get; }

    /// <summary>
    /// Whether the page is offered at all.
    /// </summary>
    /// <remarks>
    /// AGENTS.md P1: a feature that is switched off leaves no trace in the window — not a disabled
    /// entry, not an empty page explaining what is missing. Raise a change notification when this
    /// flips and the rail follows immediately, with no restart.
    /// </remarks>
    public virtual bool IsAvailable => true;

    /// <summary>
    /// The rail icon.
    /// </summary>
    /// <remarks>
    /// A character, not an image: it needs no asset pipeline, no licence, and no separate file
    /// per DPI, and every platform this app targets ships a font that draws it.
    /// </remarks>
    public abstract string Glyph { get; }

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
            // The user sees ex.Message; without this, that is the only place it ever existed.
            // "It said something went wrong and then I closed it" is not a bug report.
            Log.LogError(ex, "{Page} failed.", GetType().Name);

            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
