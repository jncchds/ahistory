using System.Text;

namespace Archive.Data;

/// <summary>
/// Turns what someone typed into an FTS5 MATCH expression.
/// </summary>
/// <remarks>
/// <para>
/// Raw input is never passed to MATCH. FTS5 has its own query syntax — <c>AND</c>, <c>OR</c>,
/// <c>NOT</c>, <c>NEAR</c>, <c>^</c>, <c>:</c>, <c>*</c>, parentheses — so a search for
/// <c>NEAR</c>, or for a name with a colon in it, is a syntax error rather than a search. Every
/// term is quoted, which makes it a literal phrase and neutralizes the lot.
/// </para>
/// <para>
/// Bare terms then get a <c>*</c> suffix. There is no FTS5 stemmer for Slavic languages
/// (decisions.md D8), so <c>Прага</c> would not find <c>Праге</c>; inflection there is
/// overwhelmingly suffixal, and prefix expansion recovers most of it. A quoted phrase is left
/// exact, because someone who typed quotes meant them.
/// </para>
/// </remarks>
public static class FtsQueryBuilder
{
    /// <summary>
    /// Builds a MATCH expression, or null when there is nothing to search for.
    /// </summary>
    /// <param name="input">What the user typed.</param>
    /// <param name="expandPrefixes">
    /// False to search for exactly what was typed — useful when prefix expansion produces too
    /// many matches to be useful.
    /// </param>
    public static string? Build(string? input, bool expandPrefixes = true)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var terms = Tokenize(input);
        var parts = new List<string>(terms.Count);

        foreach (var (text, wasQuoted) in terms)
        {
            var escaped = Quote(text);

            if (escaped is null)
            {
                continue;
            }

            // A phrase the user quoted is searched as they wrote it.
            parts.Add(expandPrefixes && !wasQuoted ? escaped + "*" : escaped);
        }

        // Input that was all punctuation tokenizes to nothing. Returning null means "no query"
        // rather than an expression that throws.
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>
    /// Splits on whitespace, keeping "quoted phrases" together.
    /// </summary>
    private static List<(string Text, bool WasQuoted)> Tokenize(string input)
    {
        var terms = new List<(string, bool)>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                if (inQuotes)
                {
                    Flush(terms, current, wasQuoted: true);
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                Flush(terms, current, wasQuoted: false);
                continue;
            }

            current.Append(c);
        }

        // An unclosed quote is treated as a phrase to the end of the input, which is what someone
        // halfway through typing one means.
        Flush(terms, current, wasQuoted: inQuotes);

        return terms;
    }

    private static void Flush(List<(string, bool)> terms, StringBuilder current, bool wasQuoted)
    {
        if (current.Length > 0)
        {
            terms.Add((current.ToString(), wasQuoted));
            current.Clear();
        }
    }

    /// <summary>
    /// Wraps a term as an FTS5 string, or returns null if nothing survives.
    /// </summary>
    /// <remarks>
    /// Inside an FTS5 double-quoted string, a literal double quote is written twice. Anything
    /// else — operators, punctuation, a stray caret — is just text once quoted.
    /// </remarks>
    private static string? Quote(string term)
    {
        var trimmed = term.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        // A term of pure punctuation produces no tokens, and FTS5 rejects an empty phrase.
        if (!trimmed.Any(char.IsLetterOrDigit))
        {
            return null;
        }

        return "\"" + trimmed.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
