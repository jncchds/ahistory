using System.Security.Cryptography;
using System.Text.Json;
using Archive.Data;
using Archive.Import.Telegram;
using Archive.Media;

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
public sealed class ImportRunner(Database database, IMediaStore mediaStore)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMediaStore _mediaStore = mediaStore ?? throw new ArgumentNullException(nameof(mediaStore));

    /// <summary>
    /// Imports a Telegram export folder.
    /// </summary>
    /// <param name="exportFolder">Folder containing result.json and its media directories.</param>
    /// <param name="onProgress">
    /// Called as messages are committed. The caller marshals to a UI thread if it needs to;
    /// nothing here touches one.
    /// </param>
    public ImportStats Run(
        string exportFolder,
        Action<ImportProgress>? onProgress = null,
        int batchSize = 1000)
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

        using var committer = new ImportCommitter(
            _database, TelegramNormalizer.Platform, folder, Fingerprint(files), batchSize);

        try
        {
            var sink = new CommittingSink(committer, _mediaStore, folder, onProgress);

            foreach (var file in files)
            {
                using var stream = File.OpenRead(file);
                TelegramExportReader.Read(stream, sink);
            }

            committer.Complete();
            return committer.Stats;
        }
        catch (Exception ex)
        {
            // The import row is left marked failed with its error, rather than looking as though
            // it succeeded with fewer messages than it should have.
            committer.Fail(ex.Message);
            throw;
        }
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
        Action<ImportProgress>? onProgress) : ITelegramExportSink
    {
        private string _currentChat = string.Empty;

        public void OnPersonalInformation(JsonElement element) => committer.SeedOwner(element);

        public void OnChat(TelegramChatHeader chat)
        {
            _currentChat = chat.Name ?? chat.SourceThreadId;
            committer.EnsureThread(chat.SourceThreadId, chat.ThreadKind, chat.Name);
        }

        public void OnMessage(TelegramChatHeader chat, JsonElement message)
        {
            var normalized = TelegramNormalizer.Normalize(chat, message);
            var stored = new List<StoredMedia?>(normalized.Media.Count);

            foreach (var media in normalized.Media)
            {
                stored.Add(Store(media));
            }

            committer.Add(normalized, stored);

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
                return null;
            }

            var result = mediaStore.PutFileAsync(path).GetAwaiter().GetResult();

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
