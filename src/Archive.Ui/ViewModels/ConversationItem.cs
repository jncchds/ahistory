using System.Collections.ObjectModel;
using Archive.Data;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archive.Ui.ViewModels;

/// <summary>Something in the conversation stream: a message, or a stretch of silence.</summary>
/// <remarks>
/// Derives from <see cref="ObservableObject"/> so items can raise changes — expanding a group
/// line for context happens on the item itself, not on the page.
/// </remarks>
public abstract class ConversationItem : ObservableObject;

/// <summary>
/// A gap long enough to be worth showing.
/// </summary>
/// <remarks>
/// §8: "four months, no contact" is often the most meaningful thing in a relationship's timeline,
/// and a view that only draws messages papers straight over it.
/// </remarks>
public sealed class SilenceItem(TimeSpan gap) : ConversationItem
{
    public TimeSpan Gap { get; } = gap;

    public string Text => Describe(Gap);

    private static string Describe(TimeSpan gap)
    {
        var days = (int)gap.TotalDays;

        return days switch
        {
            >= 730 => $"{days / 365} years, no contact",
            >= 365 => "a year, no contact",
            >= 60 => $"{days / 30} months, no contact",
            _ => $"{days} days, no contact",
        };
    }
}

/// <summary>A message, with the context it may need to make sense.</summary>
public sealed partial class MessageItem(PersonMessageRow row, Func<long, Task<IReadOnlyList<PersonMessageRow>>> loadContext)
    : ConversationItem
{
    public PersonMessageRow Row { get; } = row;

    /// <summary>Messages either side of this one in its own thread, once asked for.</summary>
    public ObservableCollection<PersonMessageRow> Context { get; } = [];

    /// <summary>
    /// Group lines can be expanded; direct messages have nothing to expand into.
    /// </summary>
    public bool CanExpand => Row.IsFromGroup;

    /// <summary>Yours on the right, theirs on the left.</summary>
    public HorizontalAlignment Alignment =>
        Row.FromOwner ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>
    /// True for your own messages.
    /// </summary>
    /// <remarks>
    /// A flag rather than a brush: colour and corner radius are decided by a style, so the view
    /// model never has to look a resource up and the palette can change without touching it.
    /// </remarks>
    public bool IsOutgoing => Row.FromOwner;

    /// <summary>
    /// Whether to label the bubble with the room it was said in.
    /// </summary>
    /// <remarks>
    /// Only group lines get a label, and it names the <em>room</em>, not the speaker — which is
    /// why it is not called ShowsSender any more. In a view of one person every incoming message
    /// is from that same person, so their name on every bubble is a column of one repeated word;
    /// where it was said is the part that varies (§4). The speaker's own name does appear in this
    /// view, on the context rows a group line expands into, where the other voices are new.
    /// </remarks>
    public bool ShowsThreadLabel => Row.IsFromGroup;

    /// <summary>
    /// The room this was said in, for a group line.
    /// </summary>
    /// <remarks>
    /// A group can have no title, and a blank label above a bubble reads as a rendering fault
    /// rather than as the absence it is.
    /// </remarks>
    public string ThreadLabel =>
        string.IsNullOrWhiteSpace(Row.ThreadTitle) ? "a group" : Row.ThreadTitle;

    /// <summary>
    /// A readable time, not the stored ISO string.
    /// </summary>
    /// <remarks>
    /// Dated as well as timed, because this is an archive: in a live chat "9:48" means today, and
    /// here it could mean any day in ten years.
    /// </remarks>
    public string Timestamp =>
        DateTimeOffset.FromUnixTimeSeconds(Row.SentAtUnix).LocalDateTime.ToString("d MMM yyyy, HH:mm");

    /// <summary>
    /// True for the one message a search result was opened on.
    /// </summary>
    /// <remarks>
    /// Arriving in the middle of a conversation puts a hundred messages on screen, and without a
    /// mark the one that was searched for is indistinguishable from the ninety-nine around it.
    /// </remarks>
    [ObservableProperty]
    private bool _isRevealed;

    /// <summary>
    /// True on the first message of a stretch a model has not read yet, while AI is on (§7).
    /// </summary>
    [ObservableProperty]
    private bool _isUnreadByAi;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoadingContext;

    /// <summary>
    /// Fetches what was said around this message (§4).
    /// </summary>
    /// <remarks>
    /// On demand rather than up front: most group lines are never expanded, and fetching context
    /// for all of them would multiply every page load by the context radius.
    /// </remarks>
    [RelayCommand]
    private async Task ToggleContext()
    {
        if (IsExpanded)
        {
            IsExpanded = false;
            return;
        }

        if (Context.Count == 0)
        {
            IsLoadingContext = true;

            try
            {
                foreach (var message in await loadContext(Row.Id).ConfigureAwait(true))
                {
                    Context.Add(message);
                }
            }
            finally
            {
                IsLoadingContext = false;
            }
        }

        IsExpanded = true;
    }
}
