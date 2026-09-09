namespace Archive.Import.Telegram;

/// <summary>
/// The identifying fields of one chat, read before its messages are streamed.
/// </summary>
/// <param name="Name">Display name. Null for some channels and deleted accounts.</param>
/// <param name="Type">Telegram's chat type, e.g. personal_chat, private_group, saved_messages.</param>
/// <param name="Id">Telegram's numeric chat id, as text. Null in older exports.</param>
/// <param name="IsLeft">True when the chat came from the export's left_chats section.</param>
public sealed record TelegramChatHeader(string? Name, string? Type, string? Id, bool IsLeft)
{
    /// <summary>
    /// The thread kind this chat maps to.
    /// </summary>
    /// <remarks>
    /// saved_messages is kept as a conversation with yourself (decisions.md D6): in a real export
    /// it is usually the densest personal-notes archive present.
    /// </remarks>
    public string ThreadKind => Type switch
    {
        "saved_messages" => "saved",
        "personal_chat" or "bot_chat" => "dm",
        "private_group" or "private_supergroup" or "public_supergroup" => "group",
        "channel" or "private_channel" or "public_channel" => "channel",
        _ => "group",
    };

    /// <summary>
    /// A stable identifier for the thread even when the export omits the numeric id.
    /// </summary>
    /// <remarks>
    /// Older exports have no chat id at all. Falling back to the name keeps such a chat
    /// importable and, more importantly, keeps re-import idempotent: the same export produces
    /// the same source id both times. It is weaker than a real id — renaming the chat in
    /// Telegram and re-exporting produces a second thread — which is why the id is preferred
    /// whenever it exists.
    /// </remarks>
    public string SourceThreadId =>
        !string.IsNullOrWhiteSpace(Id) ? Id
        : !string.IsNullOrWhiteSpace(Name) ? "name:" + Name
        : throw new InvalidOperationException("Chat has neither an id nor a name.");
}
