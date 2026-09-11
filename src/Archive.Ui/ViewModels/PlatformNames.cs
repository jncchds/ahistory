namespace Archive.Ui.ViewModels;

/// <summary>What to call a platform in a sentence, from the id its threads are keyed by.</summary>
public static class PlatformNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["telegram"] = "Telegram",
        ["imessage"] = "iMessage",
        ["vk"] = "VK",
        ["whatsapp"] = "WhatsApp",
        ["sms"] = "SMS",
        ["googlechat"] = "Google Chat",
        ["googlevoice"] = "Google Voice",
        ["hangouts"] = "Hangouts",
        ["messenger"] = "Messenger",
        ["instagram"] = "Instagram",
        ["discord"] = "Discord",
        ["slack"] = "Slack",
        ["skype"] = "Skype",
        ["qip"] = "QIP",
    };

    /// <summary>The platform a thread belongs to, named for a person to read.</summary>
    /// <remarks>
    /// Thread ids are <c>platform:source-thread-id</c> (<c>ImportCommitter.ThreadId</c>), so the
    /// platform is whatever precedes the first colon.
    /// </remarks>
    public static string OfThread(string threadId)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var colon = threadId.IndexOf(':', StringComparison.Ordinal);
        var platform = colon > 0 ? threadId[..colon] : threadId;

        return Known.TryGetValue(platform, out var name)
            ? name
            : platform.Length == 0 ? "the platform" : char.ToUpperInvariant(platform[0]) + platform[1..];
    }

    /// <summary>
    /// The sentence a message the platform has deleted carries.
    /// </summary>
    /// <remarks>
    /// Says both halves: that it is gone from the platform, and that the archive still has it.
    /// "Deleted" on its own reads as though the archive lost it — the opposite of what happened.
    /// The date is when the deletion was noticed, and says so, because no platform reports when
    /// the deletion itself happened.
    /// </remarks>
    public static string DeletedLabel(string threadId, string observedUtc)
    {
        var noticed = DateTimeOffset.TryParse(
            observedUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var at)
            ? $" · noticed {at.LocalDateTime:d MMM yyyy}"
            : string.Empty;

        return $"deleted in {OfThread(threadId)}, kept here{noticed}";
    }
}
