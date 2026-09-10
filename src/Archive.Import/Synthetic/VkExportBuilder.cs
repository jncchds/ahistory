using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace Archive.Import.Synthetic;

/// <summary>One VK account as an archive page links to it.</summary>
/// <param name="Link">
/// The profile path VK writes — <c>id222</c> for a person, <c>club3001</c> or <c>public9</c> for a
/// community. Null means the sender is you: VK omits the link entirely on your own messages rather
/// than linking to your own profile.
/// </param>
public sealed record VkAuthor(string Name, string? Link)
{
    public static VkAuthor Person(long id, string name) =>
        new(name, "id" + id.ToString(CultureInfo.InvariantCulture));

    public static VkAuthor Club(long id, string name) =>
        new(name, "club" + id.ToString(CultureInfo.InvariantCulture));

    /// <summary>You. VK leaves your own messages unattributed, and that absence is the marker.</summary>
    public static VkAuthor You { get; } = new("Вы", null);

    /// <summary>A name with a link this reader cannot resolve to a profile id.</summary>
    public static VkAuthor Unresolvable(string name, string href) => new(name, href);
}

/// <summary>
/// Builds a VKontakte data archive: <c>messages/&lt;peer id&gt;/messages0.html</c>.
/// </summary>
/// <remarks>
/// <para>
/// The markup is written out rather than templated from a real page, so every part a test depends
/// on — the breadcrumb the title comes from, the missing anchor that means "me", the edit marker
/// that sits between the name and the date — is visible here as the thing it is. See
/// <see cref="TelegramExportBuilder"/> for why exports are built in code rather than committed.
/// </para>
/// <para>
/// Dates are written as VK writes them: Russian, with an abbreviated month and no offset.
/// </para>
/// </remarks>
public sealed class VkExportBuilder
{
    private static readonly string[] MonthNames =
    [
        "янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек",
    ];

    private readonly List<(string PeerId, string Title, List<string> Messages, int Page)> _conversations = [];

    public static VkExportBuilder New() => new();

    /// <summary>
    /// One conversation folder.
    /// </summary>
    /// <param name="peerId">
    /// VK's own peer id, and the folder name. Negative ids are groups and multi-person chats;
    /// positive ones are people.
    /// </param>
    /// <param name="page">
    /// Which page file this is. VK paginates in fifties and names the file after the message the
    /// page starts at, so a second page is <c>messages50.html</c>.
    /// </param>
    public VkExportBuilder Conversation(
        string peerId, string title, Action<VkConversationBuilder> messages, int page = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(messages);

        var written = new List<string>();
        messages(new VkConversationBuilder(written));

        _conversations.Add((peerId, title, written, page));

        return this;
    }

    /// <summary>Writes the <c>messages</c> tree into <paramref name="folder"/> and returns it.</summary>
    public string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        foreach (var (peerId, title, messages, page) in _conversations)
        {
            var directory = Path.Combine(folder, "messages", peerId);
            Directory.CreateDirectory(directory);

            var file = Path.Combine(
                directory, $"messages{page.ToString(CultureInfo.InvariantCulture)}.html");

            File.WriteAllText(file, Page(title, messages), new UTF8Encoding(false));
        }

        return folder;
    }

    private static string Page(string title, List<string> messages)
    {
        var builder = new StringBuilder()
            .AppendLine("<!DOCTYPE html>")
            .AppendLine("<html><head><meta charset=\"utf-8\"><title>Сообщения</title></head>")
            .AppendLine("<body>")
            // The breadcrumb is where the conversation's name comes from, and it is the last
            // crumb rather than the first that names it.
            .AppendLine("<div class=\"ui_crumbs\">")
            .AppendLine("  <div class=\"ui_crumb\">Архив</div>")
            .AppendLine("  <div class=\"ui_crumb\">Сообщения</div>")
            .Append("  <div class=\"ui_crumb\">").Append(Escape(title)).AppendLine("</div>")
            .AppendLine("</div>");

        foreach (var message in messages)
        {
            builder.AppendLine(message);
        }

        return builder.AppendLine("</body></html>").ToString();
    }

    internal static string Escape(string text) => HtmlEncoder.Default.Encode(text);

    /// <summary>VK's own date format: <c>1 янв 2020 в 12:34:56</c>.</summary>
    internal static string RussianDate(DateTimeOffset at, bool withSeconds = true)
    {
        var builder = new StringBuilder()
            .Append(at.Day.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(MonthNames[at.Month - 1])
            .Append(' ')
            .Append(at.Year.ToString(CultureInfo.InvariantCulture))
            .Append(" в ")
            .Append(at.Hour.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(at.Minute.ToString("00", CultureInfo.InvariantCulture));

        if (withSeconds)
        {
            builder.Append(':').Append(at.Second.ToString("00", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

/// <summary>The messages of one VK conversation page.</summary>
public sealed class VkConversationBuilder(List<string> messages)
{
    /// <summary>
    /// One message.
    /// </summary>
    /// <param name="editedAt">
    /// When set, adds the <c>message-edited</c> span VK puts between the name and the date — the
    /// thing that has to be removed before the date can be read.
    /// </param>
    /// <param name="attachment">
    /// An attachment description, as VK writes it. The archive names attachments but never ships
    /// the files.
    /// </param>
    /// <param name="withSeconds">
    /// False writes a date without seconds, which some archives do and which the reader has to
    /// tolerate.
    /// </param>
    public VkConversationBuilder Message(
        long id,
        DateTimeOffset at,
        VkAuthor author,
        string text,
        DateTimeOffset? editedAt = null,
        (string Description, string Href)? attachment = null,
        bool withSeconds = true)
    {
        ArgumentNullException.ThrowIfNull(author);

        var builder = new StringBuilder()
            .AppendLine("<div class=\"item\">")
            .Append("  <div class=\"message\" data-id=\"")
            .Append(id.ToString(CultureInfo.InvariantCulture))
            .AppendLine("\">")
            .Append("    <div class=\"message__header\">");

        if (author.Link is null)
        {
            builder.Append(VkExportBuilder.Escape(author.Name));
        }
        else
        {
            builder.Append("<a href=\"https://vk.com/")
                   .Append(VkExportBuilder.Escape(author.Link))
                   .Append("\">")
                   .Append(VkExportBuilder.Escape(author.Name))
                   .Append("</a>");
        }

        if (editedAt is { } edited)
        {
            builder.Append("<span class=\"message-edited\" title=\"")
                   .Append(VkExportBuilder.RussianDate(edited))
                   .Append("\">ред.</span>");
        }

        builder.Append(", ")
               .Append(VkExportBuilder.RussianDate(at, withSeconds))
               .AppendLine("</div>")
               .Append("    ")
               .AppendLine(VkExportBuilder.Escape(text));

        if (attachment is { } present)
        {
            builder.AppendLine("    <div class=\"kludges\">")
                   .AppendLine("      <div class=\"attachment\">")
                   .Append("        <div class=\"attachment__description\">")
                   .Append(VkExportBuilder.Escape(present.Description))
                   .AppendLine("</div>")
                   .Append("        <a href=\"")
                   .Append(VkExportBuilder.Escape(present.Href))
                   .AppendLine("\">фото</a>")
                   .AppendLine("      </div>")
                   .AppendLine("    </div>");
        }

        builder.AppendLine("  </div>")
               .Append("</div>");

        messages.Add(builder.ToString());

        return this;
    }

    /// <summary>
    /// A message with no <c>data-id</c>.
    /// </summary>
    /// <remarks>
    /// Without VK's own id there is no stable key, so the reader refuses rather than inventing one
    /// and making the import non-idempotent. Building it deliberately is how that gets tested.
    /// </remarks>
    public VkConversationBuilder MessageWithNoId(DateTimeOffset at, VkAuthor author, string text)
    {
        messages.Add($"""
            <div class="item">
              <div class="message">
                <div class="message__header">{VkExportBuilder.Escape(author.Name)}, {VkExportBuilder.RussianDate(at)}</div>
                {VkExportBuilder.Escape(text)}
              </div>
            </div>
            """);

        return this;
    }

    /// <summary>A message whose header carries something that is not a date this reader knows.</summary>
    public VkConversationBuilder MessageWithHeader(long id, string header, string text)
    {
        messages.Add($"""
            <div class="item">
              <div class="message" data-id="{id.ToString(CultureInfo.InvariantCulture)}">
                <div class="message__header">{header}</div>
                {VkExportBuilder.Escape(text)}
              </div>
            </div>
            """);

        return this;
    }
}
