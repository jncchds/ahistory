using Archive.Ui.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Archive.Ui.Views;

/// <summary>
/// The conversation, paged by scrolling.
/// </summary>
/// <remarks>
/// The paging itself belongs to the view model; where the reader has got to is a fact about the
/// window, which is why the trigger lives here. This is also the only place that can put the
/// scroll position back after a page is inserted above it.
/// </remarks>
public sealed partial class PersonView : UserControl
{
    /// <summary>
    /// How close to an end counts as reaching it.
    /// </summary>
    /// <remarks>
    /// Roughly a screenful of bubbles, so the next page is usually already there by the time the
    /// reader arrives — loading exactly at the edge shows them the edge first.
    /// </remarks>
    private const double LoadWithin = 400;

    private PersonViewModel? _model;
    private ScrollViewer? _scroll;

    /// <summary>
    /// True from the moment a page is asked for until its scroll position has been restored.
    /// </summary>
    /// <remarks>
    /// One flick of a wheel raises a great many scroll events. Without this, reaching the top of
    /// the stream starts a dozen overlapping page loads rather than one.
    /// </remarks>
    private bool _isPaging;

    /// <summary>
    /// Which scroll was asked for most recently.
    /// </summary>
    /// <remarks>
    /// Opening a conversation scrolls to the end; opening a search result scrolls to one message.
    /// Arriving from a search does both — navigating to the page reloads it, and the reveal
    /// follows — so each intent takes a number and only the newest one is allowed to land.
    /// Without this the reload's scroll-to-end runs last and the message just revealed is a
    /// hundred bubbles above the window.
    /// </remarks>
    private int _scrollIntent;

    public PersonView()
    {
        InitializeComponent();

        // ScrollChanged bubbles, so the ItemsControl hears its own templated ScrollViewer without
        // anything here having to reach into the template to find it.
        Stream.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        _model = DataContext as PersonViewModel;

        if (_model is not null)
        {
            _model.StreamReloaded += OnStreamReloaded;
            _model.MessageRevealed += OnMessageRevealed;
        }
    }

    private void Unsubscribe()
    {
        if (_model is not null)
        {
            _model.StreamReloaded -= OnStreamReloaded;
            _model.MessageRevealed -= OnMessageRevealed;
        }
    }

    /// <summary>A conversation opens on what was said last, the way a chat client does.</summary>
    private void OnStreamReloaded(object? sender, EventArgs e)
    {
        var intent = ++_scrollIntent;

        AfterLayout(() =>
        {
            if (intent == _scrollIntent)
            {
                Scroller()?.ScrollToEnd();
            }
        });
    }

    private void OnMessageRevealed(object? sender, long messageId)
    {
        var intent = ++_scrollIntent;

        // Twice, because the first pass positions against a virtualizing panel's estimate of how
        // tall the messages it has not realized yet are. The second lands on the real thing.
        AfterLayout(() =>
        {
            ScrollTo(messageId, intent);
            AfterLayout(() => ScrollTo(messageId, intent));
        });
    }

    private void ScrollTo(long messageId, int intent)
    {
        if (_model is null || intent != _scrollIntent)
        {
            return;
        }

        for (var i = 0; i < _model.Items.Count; i++)
        {
            if (_model.Items[i] is MessageItem message && message.Row.Id == messageId)
            {
                Stream.ScrollIntoView(i);
                return;
            }
        }
    }

    private void OnScrollChanged(object? sender, EventArgs e) => _ = PageIfAtAnEdgeAsync();

    private async Task PageIfAtAnEdgeAsync()
    {
        if (_isPaging || _model is null || Scroller() is not { } scroll)
        {
            return;
        }

        var offset = scroll.Offset.Y;
        var furthest = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

        if (offset <= LoadWithin && _model.HasMore)
        {
            await PageAsync(scroll, _model.LoadMoreCommand.ExecuteAsync(null), keepPlace: true)
                .ConfigureAwait(true);
        }
        else if (furthest - offset <= LoadWithin && _model.HasNewer)
        {
            // Nothing to restore: a page added below the reader does not move what they are on.
            await PageAsync(scroll, _model.LoadNewerCommand.ExecuteAsync(null), keepPlace: false)
                .ConfigureAwait(true);
        }
    }

    private async Task PageAsync(ScrollViewer scroll, Task load, bool keepPlace)
    {
        _isPaging = true;

        var before = scroll.Extent.Height;

        try
        {
            await load.ConfigureAwait(true);
        }
        finally
        {
            if (keepPlace)
            {
                // A page inserted above the reader pushes everything down by its own height. Left
                // alone that carries the message they were reading off the top of the window, and
                // leaves them at the top again — which immediately asks for another page.
                AfterLayout(() =>
                {
                    var grew = scroll.Extent.Height - before;

                    if (grew > 0)
                    {
                        scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + grew);
                    }

                    _isPaging = false;
                });
            }
            else
            {
                _isPaging = false;
            }
        }
    }

    /// <summary>
    /// The ScrollViewer the ItemsControl's own template puts around the items.
    /// </summary>
    /// <remarks>
    /// Resolved lazily: the template has not been applied when this view is constructed, and the
    /// items panel has to stay inside a ScrollViewer of its own for virtualization to work at all.
    /// </remarks>
    private ScrollViewer? Scroller() =>
        _scroll ??= Stream.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>Runs once the layout that this change caused has been done.</summary>
    private static void AfterLayout(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.Loaded);
}
