using System.Text;
using Archive.Ai.Extraction;

namespace Archive.Ai.Search;

/// <summary>
/// What a session is embedded as, and how a query is phrased to meet it.
/// </summary>
/// <remarks>
/// <para>
/// Sessions, not messages (spec §5): "ok lol" embeds to noise, and a conversation embeds to what it
/// was about. The text is built through the same window extraction uses, so everything left out of
/// reading is left out of this too — a person excluded is not sent to an embedding endpoint either.
/// </para>
/// <para>
/// Bump <see cref="Version"/> whenever what is embedded changes, and every vector is rebuilt the
/// next time the runner looks; the old ones keep answering until then.
/// </para>
/// </remarks>
public sealed class SessionText(ExtractionWindows windows)
{
    public const string Version = "1";

    /// <summary>
    /// How much of a session is embedded.
    /// </summary>
    /// <remarks>
    /// The start of a conversation says what it was about, and embedding models truncate well
    /// before a long session ends anyway — silently, which is worse than doing it here on purpose.
    /// </remarks>
    private const int MaxLength = 4_000;

    private readonly ExtractionWindows _windows = windows ?? throw new ArgumentNullException(nameof(windows));

    /// <summary>The text for a session, or null when it must not be sent anywhere.</summary>
    public string? For(string sessionId, string model)
    {
        var window = _windows.Load(sessionId);

        if (window is null)
        {
            return null;
        }

        var text = new StringBuilder();

        foreach (var message in window.Messages)
        {
            if (text.Length >= MaxLength)
            {
                break;
            }

            text.Append(message.SenderName).Append(": ").AppendLine(message.Text);
        }

        var body = text.Length > MaxLength ? text.ToString(0, MaxLength) : text.ToString();

        return DocumentPrefix(model) + body;
    }

    /// <summary>A query, phrased the way the model expects queries to be.</summary>
    public static string Query(string model, string query) => QueryPrefix(model) + query;

    /// <summary>
    /// The instruction some embedding models are trained to expect before a document.
    /// </summary>
    /// <remarks>
    /// Nomic and E5 models are trained with a task prefix on both sides, and without it their
    /// queries and documents land in slightly different places — search that works, just noticeably
    /// worse. The two families here are the ones this app is likely to meet; anything else gets the
    /// text as it is, which is right for most models.
    /// </remarks>
    private static string DocumentPrefix(string model) =>
        model.Contains("nomic", StringComparison.OrdinalIgnoreCase) ? "search_document: "
        : model.Contains("e5", StringComparison.OrdinalIgnoreCase) ? "passage: "
        : string.Empty;

    private static string QueryPrefix(string model) =>
        model.Contains("nomic", StringComparison.OrdinalIgnoreCase) ? "search_query: "
        : model.Contains("e5", StringComparison.OrdinalIgnoreCase) ? "query: "
        : string.Empty;
}
