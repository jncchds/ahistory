using Archive.Ai.Llm;

namespace Archive.Ai;

/// <summary>
/// Whether the user has agreed to archive text being sent to the endpoint configured now.
/// </summary>
/// <remarks>
/// <para>
/// Segmentation needs nobody's permission: it runs locally and reads nothing out of the archive
/// that it did not already hold. Extraction sends correspondence to an endpoint. So the first time
/// it would run against a given endpoint, the user is shown how much, roughly what it costs and
/// where it goes, and asked (ai-plan.md §11.2) — and only after that is extraction queued on its
/// own, after an import or a restart.
/// </para>
/// <para>
/// The agreement is recorded against the endpoint it was given for. Pointing the settings at a
/// different one makes it stale, which is the point: consenting to a model on your own machine
/// is not consenting to a hosted API.
/// </para>
/// </remarks>
public static class AiConsent
{
    /// <summary>Where extraction would send text, with the family's default filled in.</summary>
    public static string Destination(AiSettings settings, ILlmProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(factory);

        return factory.Create(settings).BaseUrl.ToString();
    }

    /// <summary>True when the text never leaves this machine.</summary>
    /// <remarks>
    /// Loopback only. An address on the local network is still another machine, and the sentence
    /// shown for it says so — a LAN box is someone's box, and it is not always the user's.
    /// </remarks>
    public static bool IsLocal(AiSettings settings, ILlmProviderFactory factory) =>
        new Uri(Destination(settings, factory)).IsLoopback;

    /// <summary>True when extraction may be queued without asking.</summary>
    public static bool CoversExtraction(AiSettings settings, ILlmProviderFactory factory) =>
        settings.IsUsable
        && string.Equals(
            settings.ExtractionConfirmedFor,
            Destination(settings, factory),
            StringComparison.OrdinalIgnoreCase);
}
