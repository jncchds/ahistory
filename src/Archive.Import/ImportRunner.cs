using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Archive.Data;
using Archive.Import.Telegram;
using Archive.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Import;

/// <summary>Progress while an import runs.</summary>
public sealed record ImportProgress(string CurrentChat, long MessagesSeen, long MessagesInserted);

/// <summary>
/// Runs one import: read the export, normalize it, store its media, commit it.
/// </summary>
/// <remarks>
/// §2: the export folder is ingested as a unit. The JSON references media by relative path, so
/// the files only make sense alongside it, and a large account is split across
/// <c>result.json</c>, <c>result2.json</c> and so on — all of which are one logical import with
/// one import row.
/// </remarks>
public sealed class ImportRunner(Database database, IMediaStore mediaStore, ILogger<ImportRunner>? logger = null)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMediaStore _mediaStore = mediaStore ?? throw new ArgumentNullException(nameof(mediaStore));
    private readonly ILogger _log = logger ?? NullLogger<ImportRunner>.Instance;

    /// <summary>
    /// Inspects an export folder without writing anything, so the caller can ask the user which
    /// source it belongs to.
    /// </summary>
    /// <remarks>
    /// Reads only far enough to find the export's personal_information block — a few kilobytes,
    /// not a pass over the whole file.
    /// </remarks>
    public ImportPreview Preview(string exportFolder)
    {
        var (folder, files) = Locate(exportFolder);

        return ImportSourceResolver.Preview(_database, folder, files);
    }

    /// <summary>
    /// Imports a Telegram export folder.
    /// </summary>
    /// <param name="exportFolder">Folder containing result.json and its media directories.</param>
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
    /// Whether to keep each message's original export JSON (§1). The largest single thing in the
    /// database; `ahistory stats` reports how much.
    /// </param>
    public ImportStats Run(
        string exportFolder,
        Action<ImportProgress>? onProgress = null,
        int batchSize = 1000,
        string? sourceId = null,
        bool storeRawJson = true)
    {
        var (folder, files) = Locate(exportFolder);
        var preview = ImportSourceResolver.Preview(_database, folder, files);

        var resolvedSource = sourceId ?? preview.SuggestedSourceId;

        // The label only lands if the source is new; an existing one keeps whatever it is called.
        var label = string.Equals(resolvedSource, preview.SuggestedSourceId, StringComparison.Ordinal)
            ? preview.SuggestedLabel
            : null;

        using var committer = new ImportCommitter(
            _database, TelegramNormalizer.Platform, resolvedSource, label, folder,
            Fingerprint(files), batchSize, storeRawJson);

        // The folder path is the one piece of user-chosen text logged here on purpose: an import
        // that cannot say where it read from is very hard to diagnose. Nothing from inside the
        // export is written to the log.
        _log.LogInformation(
            "Import {ImportId} starting from {ExportFolder} ({FileCount} file(s)) into source {SourceId}.",
            committer.ImportId, folder, files.Length, resolvedSource);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sink = new CommittingSink(committer, _mediaStore, folder, onProgress, _log);

            foreach (var file in files)
            {
                using var stream = File.OpenRead(file);
                TelegramExportReader.Read(stream, sink);
            }

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

    /// <summary>Validates an export folder and finds its JSON files.</summary>
    private static (string Folder, string[] Files) Locate(string exportFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportFolder);

        var folder = Path.GetFullPath(exportFolder);

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"No such export folder: {folder}");
        }

        var files = ResultFiles(folder);

        if (files.Length == 0)
        {
            throw new InvalidDataException(
                $"'{folder}' contains no result.json. Export from Telegram Desktop as JSON, not HTML (§2).");
        }

        return (folder, files);
    }

    /// <summary>
    /// The export's JSON files, in the order Telegram numbers them.
    /// </summary>
    /// <remarks>
    /// Ordinal sort on a zero-padded name would be wrong for result10.json, so the numeric
    /// suffix is compared as a number. Messages are keyed by uid, so order does not affect
    /// correctness — but it does affect which import is recorded as discovering a row.
    /// </remarks>
    private static string[] ResultFiles(string folder) =>
        [.. Directory.EnumerateFiles(folder, "result*.json")
            .OrderBy(SuffixNumber)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal)];

    private static int SuffixNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = name.AsSpan("result".Length);

        return digits.Length > 0 && int.TryParse(digits, out var n) ? n : 1;
    }

    /// <summary>
    /// Identifies the export's contents, so re-importing the same bytes is recognizable.
    /// </summary>
    private static string Fingerprint(string[] files)
    {
        using var hasher = SHA256.Create();

        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            var digest = SHA256.HashData(stream);
            hasher.TransformBlock(digest, 0, digest.Length, null, 0);
        }

        hasher.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(hasher.Hash!);
    }

    private sealed class CommittingSink(
        ImportCommitter committer,
        IMediaStore mediaStore,
        string exportFolder,
        Action<ImportProgress>? onProgress,
        ILogger log) : ITelegramExportSink
    {
        private string _currentChat = string.Empty;

        public void OnPersonalInformation(JsonElement element) => committer.SeedOwner(element);

        public void OnChat(TelegramChatHeader chat)
        {
            _currentChat = chat.Name ?? chat.SourceThreadId;
            committer.EnsureThread(chat.SourceThreadId, chat.ThreadKind, chat.Name);

            // The chat's id and kind, never its name: a list of who someone talks to is exactly
            // the sort of thing a log must not quietly accumulate.
            log.LogDebug(
                "Reading chat {ThreadId} ({ThreadKind}).", chat.SourceThreadId, chat.ThreadKind);
        }

        public void OnMessage(TelegramChatHeader chat, JsonElement message)
        {
            var normalized = TelegramNormalizer.Normalize(chat, message);

            // Media is resolved lazily: the committer only asks when the message is genuinely
            // new or changed. On a re-import that is almost never, which is the difference
            // between re-hashing a whole media folder and touching none of it.
            committer.Add(normalized, () =>
            {
                var stored = new List<StoredMedia?>(normalized.Media.Count);

                foreach (var media in normalized.Media)
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
        /// Three outcomes, all normal: the export omitted the file (§2's sentinel), the file is
        /// referenced but absent from the folder, or it is present and stored. None of them is an
        /// import failure — a missing photo must not cost you the message it was attached to.
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
                // One unreadable file must not cost the whole import. §2's principle applied to
                // a different failure: a missing photo never costs you the message.
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
