using System.Globalization;

namespace Archive.Ai.Sessions;

/// <summary>
/// Decides whether a session is worth spending a model call on (§6.2).
/// </summary>
/// <remarks>
/// <para>
/// Most of a real archive is logistics: "on my way", "ok", a sticker, a photo with no caption.
/// Reading all of it with a model costs the same as reading the parts that carry something and
/// produces nothing, which is why the spec puts a cheap classifier in front of the expensive one
/// and expects 20–30% to survive it.
/// </para>
/// <para>
/// Deliberately heuristics and not a model. It runs over every session in the archive, it has to
/// be free, and being wrong is cheap in one direction: a logistics session let through costs one
/// call, while a substantive one filtered out is invisible. So the rules err towards letting
/// things through — the thresholds below are low on purpose.
/// </para>
/// <para>
/// Unicode-aware throughout. A word is a run of letters or digits by
/// <see cref="char.IsLetterOrDigit(char)"/>, not <c>[a-z]</c>: half of a real archive here is
/// Cyrillic, and an ASCII tokenizer would classify all of it as empty.
/// </para>
/// </remarks>
public static class SessionFilter
{
    /// <summary>
    /// Which rules produced a verdict.
    /// </summary>
    /// <remarks>
    /// Stored on every session and folded into the segmentation job's input hash, so changing the
    /// rules here re-queues the archive rather than leaving old verdicts standing under new rules.
    /// Version 2 added the per-message word threshold; version 1 counted distinct words flat and
    /// so grew more certain the longer a logistics session ran.
    /// </remarks>
    public const string Version = "2";

    /// <summary>How many distinct words a session needs before it can carry anything.</summary>
    private const int DistinctWords = 20;

    /// <summary>
    /// And how many per message, so the count is about variety rather than length.
    /// </summary>
    /// <remarks>
    /// A bare threshold on distinct words is a threshold on session length wearing a disguise:
    /// forty exchanges of "on my way" / "ok" / "5 min" clear twenty distinct words easily, and the
    /// longer a logistics session runs the more certainly it passes. Measured on a synthetic
    /// archive, the flat rule called 95% of sessions worth reading — against the 20–30% §6.2
    /// expects — which is the shape of a rule that is really counting messages.
    /// </remarks>
    private const double WordsPerMessage = 1.5;

    /// <summary>How long one message has to be for the session to be worth reading regardless.</summary>
    private const int SubstantialMessage = 60;

    /// <summary>
    /// A shorter bar for a session that asks something.
    /// </summary>
    /// <remarks>
    /// "how did it go with your mother?" is nineteen characters of question and the most useful
    /// line in a week of logistics. A question is the cheapest available signal that an exchange
    /// was about something.
    /// </remarks>
    private const int QuestionWords = 8;

    /// <summary>Whether this session is worth a model call.</summary>
    /// <param name="texts">The plaintext of every message in the session, in order.</param>
    public static bool IsSubstantive(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longest = 0;
        var asked = false;
        var spoken = 0;

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            spoken++;
            longest = Math.Max(longest, text.Length);
            asked |= text.Contains('?', StringComparison.Ordinal)
                || text.Contains('？', StringComparison.Ordinal);

            foreach (var word in Words(text))
            {
                distinct.Add(word);
            }
        }

        return longest >= SubstantialMessage
            || (distinct.Count >= DistinctWords && distinct.Count >= spoken * WordsPerMessage)
            || (asked && distinct.Count >= QuestionWords);
    }

    /// <summary>Runs of letters or digits, lowercased.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && char.IsLetterOrDigit(text[i]);

            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                yield return text[start..i].ToLower(CultureInfo.InvariantCulture);

                start = -1;
            }
        }
    }
}
