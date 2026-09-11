namespace Archive.Ai;

/// <summary>Provider families. All four are OpenAI-shaped and differ only in defaults.</summary>
public enum LlmProviderKind
{
    OpenAiCompatible,
    Ollama,
    OpenAi,
    GoogleAiStudio,
}

/// <summary>
/// Everything the AI layer needs to talk to a model, and nothing about any one archive.
/// </summary>
/// <remarks>
/// <para>
/// There are no presets. The other apps this provider code came from let a user keep several
/// configurations and pick one per bot; here there is one pipeline, so a preset list would be a
/// table, a picker, a default-selection rule and a "which preset produced this fact" foreign key,
/// all to express a choice made once.
/// </para>
/// <para>
/// This lives beside the user's other configuration and <b>not</b> in the save. A save is a .db
/// plus a media folder, meant to be copied between machines and sometimes handed to someone; an
/// API key inside it travels with the correspondence. It also means one endpoint and one model
/// serve every save on the machine, which is what a user expects — the endpoint is a property of
/// their hardware, not of an archive.
/// </para>
/// <para>
/// The consequence is that <see cref="Enabled"/> is a machine setting while facts are a save
/// setting. A save carries its own opt-out, and every derived row records the model and prompt
/// version that produced it, so an archive opened on another machine still explains itself.
/// </para>
/// </remarks>
public sealed class AiSettings
{
    public const string SectionName = "Ai";

    /// <summary>
    /// The wording of the disclaimer currently shipped.
    /// </summary>
    /// <remarks>
    /// Stored as a version rather than a bool so that rewording it asks again. A user who agreed
    /// to "this runs locally" has not agreed to anything about a hosted endpoint.
    /// </remarks>
    public const int CurrentDisclaimerVersion = 1;

    /// <summary>
    /// Off until asked for.
    /// </summary>
    /// <remarks>
    /// AGENTS.md P1: with this false there is no model, no process, no network call and no AI in
    /// the window — not a disabled button and not an empty panel explaining what is missing.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>Which disclaimer the user accepted; 0 means none.</summary>
    public int DisclaimerAcknowledgedVersion { get; set; }

    public LlmProviderKind Provider { get; set; } = LlmProviderKind.OpenAiCompatible;

    /// <summary>Base URL. Empty means the family's default; a filled value always wins.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>May be empty — a local model needs no key, and a blank one omits the header.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Extraction, adjudication, rollups, diary. Must support tool calling.</summary>
    public string MainModel { get; set; } = string.Empty;

    /// <summary>
    /// Optional cheaper model for the mechanical half of the work (spec §6.5).
    /// </summary>
    /// <remarks>
    /// Blank means "use <see cref="MainModel"/>", which is the normal case: locally, a second
    /// model is either a second resident model or a swap on every call, and one larger model
    /// usually beats both. It pays for itself against a hosted provider, where the split between
    /// half a million map calls and a few hundred reduce calls is where the cost lives.
    /// </remarks>
    public string UtilityModel { get; set; } = string.Empty;

    /// <summary>Unused until A6. Blank is not an error.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// The language facts and diary entries are written in — not the language of the archive.
    /// </summary>
    /// <remarks>
    /// An archive is routinely mixed-language; the facts about it should not be. Normalized
    /// predicates stay English regardless, because they are the merge key and a merge key that
    /// changes with a UI setting is not a key.
    /// </remarks>
    public string OutputLanguage { get; set; } = "English";

    public double? Temperature { get; set; }

    public int? MaxTokens { get; set; }

    public int TimeoutMs { get; set; } = 120_000;

    /// <summary>Extra attempts after the first, for transient failures only.</summary>
    public int MaxRetries { get; set; } = 2;

    public int RetryBaseDelayMs { get; set; } = 1_000;

    /// <summary>How many model calls the background runner may have in flight.</summary>
    public int MaxParallelCalls { get; set; } = 2;

    /// <summary>Stop the run once this many tokens have been spent. 0 means no cap.</summary>
    public long TokenBudget { get; set; }

    /// <summary>
    /// Record the full request and response of every call.
    /// </summary>
    /// <remarks>
    /// Off by default and worth keeping that way: the request body is the user's correspondence,
    /// so switching this on puts the same private text in the save a second time, in a form far
    /// easier to read out of by accident. It exists because debugging a prompt without seeing what
    /// was sent is guesswork.
    /// </remarks>
    public bool RecordPromptBodies { get; set; }

    /// <summary>
    /// The endpoint the user agreed to have archive text sent to; empty until they have.
    /// </summary>
    /// <remarks>
    /// Recorded as the resolved base URL, so changing the endpoint makes it stale. Never set from
    /// the environment: like enabling AI, it is a question the app asks in the window, and a
    /// consent a shell variable can give is not one (see <see cref="AiConsent"/>).
    /// </remarks>
    public string ExtractionConfirmedFor { get; set; } = string.Empty;

    /// <summary>The model used for a given kind of work, resolving the utility fallback.</summary>
    public string ModelFor(AiWorkKind kind) =>
        kind is AiWorkKind.Utility && !string.IsNullOrWhiteSpace(UtilityModel)
            ? UtilityModel.Trim()
            : MainModel.Trim();

    /// <summary>True once the user has seen and accepted the current wording.</summary>
    public bool DisclaimerIsCurrent => DisclaimerAcknowledgedVersion >= CurrentDisclaimerVersion;

    /// <summary>
    /// True when this configuration can actually be used to do work.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Enabled"/>: a half-filled form that is switched on must not start
    /// a background drain against a blank model name.
    /// </remarks>
    public bool IsUsable =>
        Enabled && DisclaimerIsCurrent && !string.IsNullOrWhiteSpace(MainModel);

    /// <summary>
    /// Throws if the configuration cannot produce a working client.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>ArchiveOptions.Validate</c>: bad configuration fails with a sentence naming the
    /// setting rather than surfacing later as a page that does nothing.
    /// </remarks>
    public void Validate()
    {
        if (!Enabled)
        {
            // Nothing else matters, and refusing to start over a blank field on a feature the
            // user has switched off would be absurd.
            return;
        }

        if (string.IsNullOrWhiteSpace(MainModel))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MainModel)} is required when AI is enabled "
                + $"(env: AHISTORY_{SectionName}__{nameof(MainModel)}).");
        }

        if (!string.IsNullOrWhiteSpace(Endpoint)
            && (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Endpoint)} must be an http or https URL: '{Endpoint}'.");
        }

        if (TimeoutMs <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(TimeoutMs)} must be positive.");
        }

        if (MaxParallelCalls <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxParallelCalls)} must be positive.");
        }
    }

    public AiSettings Clone() => (AiSettings)MemberwiseClone();
}

/// <summary>Which of the two configured models a piece of work should use (spec §6.5).</summary>
public enum AiWorkKind
{
    /// <summary>Synthesis: rollups and diary. The good model, a few hundred calls.</summary>
    Main,

    /// <summary>Mechanical: extraction and adjudication. The cheap model, half a million calls.</summary>
    Utility,
}
