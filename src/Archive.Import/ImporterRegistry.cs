using Archive.Import.GoogleChat;
using Archive.Import.Hangouts;
using Archive.Import.Qip;
using Archive.Import.Telegram;
using Archive.Import.Vk;

namespace Archive.Import;

/// <summary>An importer that recognized a folder, and how sure it is.</summary>
public sealed record ImporterMatch(IPlatformImporter Importer, ImportDetection Detection)
{
    public string Platform => Importer.Platform;

    public string DisplayName => Importer.DisplayName;
}

/// <summary>
/// Every format the app can read, and which one a folder is.
/// </summary>
/// <remarks>
/// Detection rather than a dropdown: someone with a decade of archives has folders whose format
/// they have long forgotten, and asking them to classify it correctly before anything works is
/// asking the wrong person. Each importer answers for itself, and the most confident wins.
/// </remarks>
public sealed class ImporterRegistry
{
    /// <summary>
    /// Registration order, which is also the tie-break order.
    /// </summary>
    /// <remarks>
    /// Telegram first: it is the only one with a published format and the only one verified
    /// against real exports, so where two importers are equally confident it should be the one
    /// that wins.
    /// </remarks>
    public ImporterRegistry(IEnumerable<IPlatformImporter>? importers = null) =>
        Importers = importers?.ToArray() ??
        [
            new TelegramImporter(),
            new HangoutsImporter(),
            new GoogleChatImporter(),
            new VkImporter(),
            new QipImporter(),
        ];

    public IReadOnlyList<IPlatformImporter> Importers { get; }

    /// <summary>
    /// Works out which importer reads this folder.
    /// </summary>
    /// <remarks>
    /// Returns null when nothing recognizes it, which the caller reports as "this does not look
    /// like an export I know" — a far better failure than picking the least-wrong importer and
    /// producing an archive full of nonsense.
    /// </remarks>
    public ImporterMatch? Detect(string path) => DetectAll(path).FirstOrDefault();

    /// <summary>
    /// Every importer that recognizes something in this folder, most confident first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One folder can hold two exports. A Google Takeout carries Hangouts and Google Chat side by
    /// side, and a Meta download carries Messenger and Instagram. Returning only the best match
    /// imported half a history and said nothing about the rest — the silent loss D20 exists to
    /// prevent, arriving through the registry instead of through a reader.
    /// </para>
    /// <para>
    /// Ties keep registration order, so the first element is exactly what <see cref="Detect"/>
    /// has always returned.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ImporterMatch> DetectAll(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var matches = new List<ImporterMatch>();

        foreach (var importer in Importers)
        {
            ImportDetection detection;

            try
            {
                detection = importer.Detect(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unreadable file must not stop the others from being asked.
                continue;
            }

            if (detection.Confidence != ImportConfidence.None)
            {
                matches.Add(new ImporterMatch(importer, detection));
            }
        }

        // OrderByDescending is stable, which is what keeps registration order as the tie-break.
        return [.. matches.OrderByDescending(m => m.Detection.Confidence)];
    }

    public IPlatformImporter For(string platform) =>
        Importers.FirstOrDefault(i => i.Platform == platform)
        ?? throw new InvalidOperationException($"No importer for platform '{platform}'.");
}
