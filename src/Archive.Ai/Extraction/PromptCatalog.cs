

namespace Archive.Ai.Extraction;

/// <summary>
/// The prompts this build ships, and what version each is.
/// </summary>
/// <remarks>
/// <para>
/// <c>prompt_version</c> is recorded on every derived row, and §6.6 wants "re-run only what was
/// produced with prompt &lt; v4" to be a one-line query. That is only meaningful if a version means
/// one text forever — so editing a prompt without bumping its version silently makes "processed
/// with v1" describe two different things, with nothing anywhere able to tell them apart.
/// </para>
/// <para>
/// `PromptTests` pins the SHA-256 of every shipped version, exactly as
/// <c>MigrationTests</c> does for migrations. Changing a hash to make that test pass is the bug it
/// exists to catch.
/// </para>
/// </remarks>
public static class PromptCatalog
{
    public const string ExtractSession = "extract.session";

    public const string AdjudicateFact = "adjudicate.fact";

    /// <summary>
    /// One month with one person — the diary entry and the month rollup, which are one text.
    /// </summary>
    /// <remarks>
    /// The plan named them separately (ai-plan.md §6). Two calls producing two summaries of the same
    /// month is the forty-rewrites cost the debounce exists to avoid, paid twice by design.
    /// </remarks>
    public const string DiaryWindow = "diary.window";

    public const string RollupYear = "rollup.year";

    public const string RollupProfile = "rollup.profile";

    /// <summary>The text in a screenshot, copied out for search (spec §3).</summary>
    public const string OcrImage = "ocr.image";

    /// <summary>The version of each prompt this build carries.</summary>
    private static readonly Dictionary<string, string> Versions = new(StringComparer.Ordinal)
    {
        [ExtractSession] = "1",
        [AdjudicateFact] = "1",
        [DiaryWindow] = "1",
        [RollupYear] = "1",
        [RollupProfile] = "1",
        [OcrImage] = "1",
    };

    private static readonly string ResourcePrefix =
        typeof(PromptCatalog).Namespace + ".Prompts.";

    /// <summary>The version to record against anything this prompt produced.</summary>
    public static string VersionOf(string name) =>
        Versions.TryGetValue(name, out var version)
            ? version
            : throw new ArgumentException($"There is no prompt called '{name}'.", nameof(name));

    /// <summary>The prompt text itself.</summary>
    public static string TextOf(string name)
    {
        _ = VersionOf(name);

        var resource = ResourcePrefix + name + ".md";

        using var stream = typeof(PromptCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The prompt resource '{resource}' is missing.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>Every prompt and its text, for the test that pins them.</summary>
    internal static IEnumerable<(string Name, string Version, string Text)> All() =>
        Versions.Select(pair => (pair.Key, pair.Value, TextOf(pair.Key)));
}
