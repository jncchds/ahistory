using Archive.Data;
using Avalonia.Layout;

namespace Archive.Ui.ViewModels;

/// <summary>One message as a conversation shows it.</summary>
/// <param name="Row">The stored message.</param>
/// <param name="ShowsSender">
/// Whether this message opens a run by one person. In a group, labelling every bubble puts a
/// column of the same name down the screen; labelling none of them makes ten speakers
/// indistinguishable. A run is the middle, and it is what a messaging client does.
/// </param>
/// <param name="ShowsAvatar">
/// Whether to draw the sender's avatar. Same rule as the label, and false for your own messages —
/// they are already on the other side of the screen.
/// </param>
public sealed record ThreadMessageItem(MessageRow Row, bool ShowsSender, bool ShowsAvatar)
{
    /// <summary>Yours on the right, everyone else's on the left.</summary>
    public HorizontalAlignment Alignment =>
        Row.FromOwner ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public bool IsOutgoing => Row.FromOwner;

    public bool IsService => Row.Kind == "service";

    /// <summary>
    /// A readable time, not the stored ISO string.
    /// </summary>
    /// <remarks>
    /// Dated as well as timed, because this is an archive: in a live chat "9:48" means today, and
    /// here it could mean any day in twenty years.
    /// </remarks>
    public string Timestamp =>
        DateTimeOffset.FromUnixTimeSeconds(Row.SentAtUnix).LocalDateTime.ToString("d MMM yyyy, HH:mm");

    /// <summary>
    /// Builds a page's worth of items, deciding where each run of one speaker begins.
    /// </summary>
    /// <param name="messages">The page, in the order it will be read.</param>
    /// <param name="previous">
    /// The message that comes immediately before the page in reading order, or null at the start
    /// of the conversation. Without it, the first message of every page is always labelled — which
    /// is visible as a stutter exactly where "load older" was pressed.
    /// </param>
    /// <param name="isGroup">
    /// Senders are only labelled in a room with more than two people in it. In a direct
    /// conversation every incoming message is from the same person, and naming each one is a
    /// column of one repeated word.
    /// </param>
    public static IReadOnlyList<ThreadMessageItem> Build(
        IReadOnlyList<MessageRow> messages, MessageRow? previous, bool isGroup)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var items = new List<ThreadMessageItem>(messages.Count);
        var last = previous;

        foreach (var message in messages)
        {
            // Compared by identity rather than by name: two people can share a display name, and
            // §2's name-only identities make that likelier here than anywhere else.
            var opensRun = last is null
                || !string.Equals(last.SenderIdentityId, message.SenderIdentityId, StringComparison.Ordinal)
                || last.Kind != message.Kind;

            var labelled = isGroup && opensRun && !message.FromOwner;

            items.Add(new ThreadMessageItem(message, labelled, labelled));
            last = message;
        }

        return items;
    }
}
