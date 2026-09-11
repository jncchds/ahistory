using System.Diagnostics;
using System.Security.Cryptography;
using Archive.Data;
using Archive.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Import;

/// <summary>Progress while an import runs.</summary>
public sealed record ImportProgress(string CurrentChat, long MessagesSeen, long MessagesInserted);

/// <summary>
/// Runs one import: read the export, store its media, commit it.
/// </summary>
/// <remarks>
/// Platform-neutral. Which format a folder is gets decided by <see cref="ImporterRegistry"/>, and
/// everything from the normalized message onward — dedupe, media, sources, the schema itself — is
/// identical whichever importer produced it. §2's build order calls the second importer the thing
/// that proves the schema was right; this is where that claim is cashed.
/// </remarks>
public sealed class ImportRunner(
    Database database,
    IMediaStore mediaStore,
    ILogger<ImportRunner>? logger = null,
    ImporterRegistry? registry = null)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMediaStore _mediaStore = mediaStore ?? throw new ArgumentNullException(nameof(mediaStore));
    private readonly ILogger _log = logger ?? NullLogger<ImportRunner>.Instance;
    private readonly ImporterRegistry _registry = registry ?? new ImporterRegistry();

    public ImporterRegistry Registry => _registry;

    /// <summary>
    /// Inspects a folder without writing anything, so the caller can ask the user where it belongs.
    /// </summary>
    /// <remarks>
    /// Detection is cheap by contract — filenames and at most a few kilobytes — so this stays
    /// instant even when pointed at a folder holding a decade of archives.
    /// </remarks>
    /// <param name="exportFolder">The folder the export unpacked into.</param>
    /// <param name="platform">
    /// Which format to read, when the folder holds more than one. Null takes the most confident.
    /// </param>
    public ImportPreview Preview(string exportFolder, string? platform = null)
    {
        var (folder, match, others) = Locate(exportFolder, platform);

        return ImportSourceResolver.Preview(_database, folder, match, others);
    }

    /// <summary>
    /// Imports an export folder.
    /// </summary>
    /// <param name="exportFolder">The folder the export unpacked into.</param>
    /// <param name="onProgress">
    /// Called as messages are committed. The caller marshals to a UI thread if it needs to;
    /// nothing here touches one.
    /// </param>
    /// <param name="batchSize">Messages per transaction.</param>
    /// <param name="sourceId">
    /// The source to attribute this run to. Null takes the preview's suggestion, which is what a
    /// non-interactive caller wants; the UI passes the user's answer instead.
    /// </param>
    /// <param name="storeRawJson">
    /// Whether to keep each message's original export data (§1). The largest single thing in the
    /// database; <c>ahistory stats</c> reports how much.
    /// </param>
    /// <param name="ownerAccountId">
    /// Which account on this platform is the archive owner's, for the formats that do not say.
    /// Null leaves the importer to work it out — and to say so, rather than to invent one.
    /// </param>
    /// <param name="platform">
    /// Which format to read, when the folder holds more than one. Null takes the most confident,
    /// which is what the preview showed.
    /// </param>
    public ImportStats Run(
        string exportFolder,
        Action<ImportProgress>? onProgress = null,
        int batchSize = 1000,
        string? sourceId = null,
        bool storeRawJson = true,
        string? ownerAccountId = null,
        string? platform = null)
    {
        var (folder, match, others) = Locate(exportFolder, platform);
        var preview = ImportSourceResolver.Preview(_database, folder, match, others);

        var resolvedSource = sourceId ?? preview.SuggestedSourceId;

        // The label only lands if the source is new; an existing one keeps whatever it is called.
        var label = string.Equals(resolvedSource, preview.SuggestedSourceId, StringComparison.Ordinal)
            ? preview.SuggestedLabel
            : null;

        using var committer = new ImportCommitter(
            _database, match.Platform, resolvedSource, label, folder,
            Fingerprint(folder), batchSize, storeRawJson);

        // The folder path is the one piece of user-chosen text logged here on purpose: an import
        // that cannot say where it read from is very hard to diagnose. Nothing from inside the
        // export is written to the log.
        _log.LogInformation(
            "Import {ImportId} starting from {ExportFolder} as {Platform} ({FileCount} file(s)) into source {SourceId}.",
            committer.ImportId, folder, match.Platform, match.Detection.FileCount, resolvedSource);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sink = new CommittingSink(committer, _mediaStore, folder, onProgress, _log);

            match.Importer.Read(folder, sink, new ImportOptions(ownerAccountId));

            committer.Complete();

            var stats = committer.Stats;
            var seconds = stopwatch.Elapsed.TotalSeconds;

            _log.LogInformation(
                "Import {ImportId} finished in {ElapsedMs} ms ({Rate:F0} messages/sec): "
                + "seen {Seen}, inserted {Inserted}, skipped {Skipped}, revised {Revised}, "
                + "media stored {MediaStored}, deduplicated {MediaDeduplicated}, "
                + "missing {MediaMissing}, not found {MediaNotFound}.",
                committer.ImportId, stopwatch.ElapsedMilliseconds,
                seconds > 0 ? stats.MessagesSeen / seconds : 0,
                stats.MessagesSeen, stats.MessagesInserted, stats.MessagesSkipped, stats.MessagesRevised,
                stats.MediaStored, stats.MediaDeduplicated, stats.MediaMissing, stats.MediaNotFound);

            // Referenced but absent files mean the export folder was moved or partially copied,
            // which silently costs attachments. Worth noticing without failing the import.
            if (stats.MediaNotFound > 0)
            {
                _log.LogWarning(
                    "{Count} attachment(s) were referenced by the export but not present in the folder.",
                    stats.MediaNotFound);
            }

            return stats;
        }
        catch (Exception ex)
        {
            // The import row is left marked failed with its error, rather than looking as though
            // it succeeded with fewer messages than it should have.
            committer.Fail(ex.Message);

            _log.LogError(
                ex, "Import {ImportId} failed after {ElapsedMs} ms and {Seen} message(s).",
                committer.ImportId, stopwatch.ElapsedMilliseconds, committer.Stats.MessagesSeen);

            throw;
        }
    }

    /// <summary>Finds the folder, the importer that reads it, and any other export sharing it.</summary>
    private (string Folder, ImporterMatch Match, IReadOnlyList<ImporterMatch> Others) Locate(
        string exportFolder, string? platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportFolder);

        var folder = Path.GetFullPath(exportFolder);

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"No such export folder: {folder}");
        }

        var matches = _registry.DetectAll(folder);

        var match = platform is null
            ? matches.FirstOrDefault()
            : matches.FirstOrDefault(m => m.Platform == platform)
                ?? throw new InvalidDataException(
                    $"'{folder}' holds no {platform} export. "
                    + (matches.Count == 0
                        ? "Nothing in it was recognized."
                        : "It holds: " + string.Join(", ", matches.Select(m => m.Platform)) + "."));

        if (match is null)
        {
            // Naming what it looked for beats "unsupported format": most of the time the folder
            // is one level up or down from the right one, and this says so.
            throw new InvalidDataException(
                $"'{folder}' does not look like an export this app can read. Expected one of: "
                + string.Join(", ", _registry.Importers.Select(i => i.DisplayName))
                + ". Point at the folder the export unpacked into — for Telegram that is the one "
                + "containing result.json, exported as JSON rather than HTML (§2).");
        }

        return (folder, match, [.. matches.Where(m => !ReferenceEquals(m.Importer, match.Importer))]);
    }

    /// <summary>
    /// Identifies the folder's contents, so re-importing the same bytes is recognizable.
    /// </summary>
    /// <remarks>
    /// Every file's name and size rather than its contents: hashing a multi-gigabyte media folder
    /// to answer "is this the same export?" would cost more than the import.
    /// </remarks>
    private static string Fingerprint(string folder)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            builder.Append(Path.GetRelativePath(folder, file))
                   .Append(':')
                   .Append(new FileInfo(file).Length)
                   .Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Commits what an importer reads.</summary>
    private sealed class CommittingSink(
        ImportCommitter committer,
        IMediaStore mediaStore,
        string exportFolder,
        Action<ImportProgress>? onProgress,
        ILogger log) : IImportSink
    {
        private readonly Dictionary<string, List<NormalizedIdentity>> _rosters = new(StringComparer.Ordinal);

        private string _currentChat = string.Empty;
        private NormalizedIdentity? _owner;

        public void OnOwner(NormalizedIdentity owner)
        {
            _owner = owner;
            committer.SeedOwner(owner);
        }

        public void OnThread(NormalizedThread thread)
        {
            _currentChat = thread.Title ?? thread.SourceThreadId;
            committer.EnsureThread(thread.SourceThreadId, thread.Kind, thread.Title);

            var roster = new List<NormalizedIdentity>(thread.Members);

            // You are in every conversation you have, whether or not you said anything in it. A
            // roster built from senders alone leaves a one-sided thread — you wrote, they never
            // replied — with nobody in it, and the per-person view finds a direct thread by its
            // participants (§4).
            if (_owner is { } owner && thread.Kind is "dm" or "saved" &&
                !roster.Any(p => p.SourceIdentityId == owner.SourceIdentityId))
            {
                roster.Add(owner);
            }

            if (roster.Count > 0)
            {
                // Held until the first message, which is where the only honest timestamp for a
                // stated participant comes from: a conversation header carries no join times.
                _rosters[thread.SourceThreadId] = roster;
            }

            // The thread's id and kind, never its name: a list of who someone talks to is exactly
            // the sort of thing a log must not quietly accumulate.
            log.LogDebug(
                "Reading thread {ThreadId} ({ThreadKind}) with {ParticipantCount} stated participant(s).",
                thread.SourceThreadId, thread.Kind, thread.Members.Count);
        }

        public void OnMessage(NormalizedThread thread, NormalizedMessage message)
        {
            if (_rosters.Remove(message.SourceThreadId, out var roster))
            {
                committer.EnsureParticipants(message.SourceThreadId, roster, message.SentAtUnix);
            }

            // Media is resolved lazily: the committer only asks when the message is genuinely
            // new or changed. On a re-import that is almost never, which is the difference
            // between re-hashing a whole media folder and touching none of it.
            committer.Add(message, () =>
            {
                var stored = new List<StoredMedia?>(message.Media.Count);

                foreach (var media in message.Media)
                {
                    stored.Add(Store(media));
                }

                return stored;
            });

            onProgress?.Invoke(new ImportProgress(
                _currentChat, committer.Stats.MessagesSeen, committer.Stats.MessagesInserted));
        }

        /// <summary>
        /// Puts one attachment in the media store, if the export actually shipped it.
        /// </summary>
        /// <remarks>
        /// Three outcomes, all normal: the export omitted the file, the file is referenced but
        /// absent from the folder, or it is present and stored. None of them is an import
        /// failure — a missing photo must not cost you the message it was attached to.
        /// </remarks>
        private StoredMedia? Store(NormalizedMedia media)
        {
            if (media.MissingReason is not null)
            {
                committer.Stats.MediaMissing++;
                return null;
            }

            var path = Path.Combine(exportFolder, media.ExportPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                committer.Stats.MediaNotFound++;

                // The relative path inside the export, not the absolute one, and never the
                // message it belonged to.
                log.LogDebug("Attachment {ExportPath} is referenced but absent.", media.ExportPath);

                return null;
            }

            MediaPutResult result;

            try
            {
                result = mediaStore.PutFileAsync(path).GetAwaiter().GetResult();
            }
            catch (IOException ex)
            {
                // One unreadable file must not cost the whole import.
                committer.Stats.MediaNotFound++;
                log.LogWarning(ex, "Could not store attachment {ExportPath}; continuing.", media.ExportPath);

                return null;
            }

            if (result.WasNew)
            {
                committer.Stats.MediaStored++;
            }
            else
            {
                committer.Stats.MediaDeduplicated++;
            }

            return new StoredMedia(result.Hash, result.Extension, result.ByteSize);
        }
    }
}
