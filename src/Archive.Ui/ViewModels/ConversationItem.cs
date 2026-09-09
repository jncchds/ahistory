using System.Collections.ObjectModel;
using Archive.Data;
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
