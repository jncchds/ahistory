using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Data;
using Archive.Media;
using Microsoft.Data.Sqlite;

namespace Archive.Ai.Attachments;

/// <summary>How reading one file ended.</summary>
public enum MediaOutcome
{
    /// <summary>Text was found and is now searchable.</summary>
    Written,

    /// <summary>Read, and there was no text in it. Recorded, so it is not read again.</summary>
    NoText,

    /// <summary>Not read: the file is not in the export, or nothing it is attached to may be sent.</summary>
    Skipped,
}

/// <summary>A file, and the message it is anchored to for search.</summary>
public sealed record MediaItem(
    string Hash,
    string Kind,
    string? Mime,
    string? Extension,
    long ByteSize,
    long MessageId,
    string ThreadId,
    string? SenderIdentityId,
    long SentAtUnix);

/// <summary>
/// Copies the text out of screenshots and transcribes voice messages, through the configured
/// endpoint (A7, spec §3).
/// </summary>
/// <remarks>
/// <para>
/// Every derived text is an artifact hanging off the media, never a replacement for the message:
/// the message keeps its caption, or its silence, exactly as it was sent. What is added is a row in
/// the search surface with its provenance on it — "from a voice message", "text in an image" — so
/// that a mishearing is visibly a mishearing rather than something someone said.
/// </para>
/// <para>
/// Through the endpoint rather than a bundled Whisper and OCR engine (decisions.md D32): a vision
/// model reads screenshots better than classic OCR does, many endpoints serve speech-to-text, and it
/// keeps native, per-platform binaries out of a build that ships to people who never switch this on.
/// </para>
/// <para>
/// Content-addressed like the files themselves: the same screenshot forwarded to three chats is
/// read once. Its search row is anchored to the earliest message that may be read — so it is found
/// once, in the first conversation it appeared in.
/// </para>
/// </remarks>
public sealed class MediaReader(Database database, IMediaStore media, AiClient client)
{
    /// <summary>Larger images are not sent: a photo that size is rarely a screenshot, and always a cost.</summary>
    private const long MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>The size OpenAI's transcription endpoint accepts, and a sensible ceiling for any other.</summary>
    private const long MaxAudioBytes = 25 * 1024 * 1024;

    /// <summary>
    /// The messages a file may be read through: the same scope as everything else that sends text.
    /// </summary>
    /// <remarks>
    /// A picture someone left out sent is theirs as much as their words are, and a voice message is
    /// their voice. Excluded senders, excluded conversations, direct conversations with someone left
    /// out, and a save that opted out — none of their files are read.
    /// </remarks>
    private const string Eligible = """
        msg.is_deleted = 0
        AND t.ai_excluded = 0
        AND NOT EXISTS (SELECT 1 FROM save_meta WHERE ai_opt_out = 1)
        AND NOT EXISTS (
            SELECT 1 FROM identity_person AS sip
            JOIN person AS sp ON sp.id = sip.person_id
            WHERE sip.identity_id = msg.sender_identity_id AND sp.ai_excluded = 1)
        AND NOT (t.kind = 'dm' AND EXISTS (
            SELECT 1
            FROM thread_participant AS tp
            JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
            JOIN person AS p ON p.id = ip.person_id
            WHERE tp.thread_id = t.id AND p.ai_excluded = 1))
        """;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    private readonly IMediaStore _media = media ?? throw new ArgumentNullException(nameof(media));

    private readonly AiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>Files of a kind that have not been read with this model, newest first.</summary>
    public IReadOnlyList<(string Hash, string InputHash, long LastUnix)> Needing(AiJobKind kind, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var (kinds, maxBytes, artifact, prompt) = Shape(kind);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT m.hash, max(msg.sent_at_unix)
            FROM media AS m
            JOIN message_media AS mm ON mm.media_hash = m.hash
            JOIN message AS msg ON msg.id = mm.message_id
            JOIN thread AS t ON t.id = msg.thread_id
            WHERE m.media_kind IN (SELECT value FROM json_each($kinds))
              AND m.byte_size <= $max
              AND {Eligible}
              AND NOT EXISTS (
                  SELECT 1 FROM derived_artifact AS d
                  WHERE d.source_media_hash = m.hash AND d.kind = $artifact
                    AND d.model = $model AND coalesce(d.prompt_version, '') = $prompt)
            GROUP BY m.hash
            ORDER BY 2 DESC;
            """;

        command.Parameters.AddWithValue("$kinds", JsonSerializer.Serialize(kinds));
        command.Parameters.AddWithValue("$max", maxBytes);
        command.Parameters.AddWithValue("$artifact", artifact);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$prompt", prompt ?? string.Empty);

        var found = new List<(string, string, long)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            found.Add((reader.GetString(0), $"{model}|{prompt}", reader.GetInt64(1)));
        }

        return found;
    }

    /// <summary>Copies the text out of an image with the vision model.</summary>
    public async Task<MediaOutcome> OcrAsync(AiSettings settings, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var item = Anchor(hash, AiJobKind.Ocr);

        if (item is null || !_media.Exists(item.Hash, item.Extension))
        {
            return MediaOutcome.Skipped;
        }

        var bytes = await Read(item, cancellationToken).ConfigureAwait(false);
        var model = settings.VisionModel.Trim();

        var completion = await _client.ChatAsync(
            settings,
            new LlmChatRequest
            {
                Model = model,
                Messages =
                [
                    LlmChatMessage.System(PromptCatalog.TextOf(PromptCatalog.OcrImage)),
                    LlmChatMessage.UserWithImages("The image:", [new LlmImage(item.Mime ?? ImageMime(item.Extension), bytes)]),
                ],
                Temperature = 0,
                MaxTokens = settings.MaxTokens,
            },
            AiPurpose.Ocr,
            AiSubject.Media(hash),
            cancellationToken).ConfigureAwait(false);

        var text = (completion.Content ?? string.Empty).Trim();

        // "NONE" is the answer the prompt asks for when there is nothing — as is an empty reply,
        // which some models give instead.
        if (string.Equals(text, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            text = string.Empty;
        }

        Record(item, "ocr", "ocr", "vision", model, completion.SystemFingerprint ?? "unknown",
            PromptCatalog.VersionOf(PromptCatalog.OcrImage), text);

        return text.Length == 0 ? MediaOutcome.NoText : MediaOutcome.Written;
    }

    /// <summary>Transcribes a voice or video message with the transcription model.</summary>
    public async Task<MediaOutcome> TranscribeAsync(AiSettings settings, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var item = Anchor(hash, AiJobKind.Transcribe);

        if (item is null || !_media.Exists(item.Hash, item.Extension))
        {
            return MediaOutcome.Skipped;
        }

        var bytes = await Read(item, cancellationToken).ConfigureAwait(false);
        var model = settings.TranscriptionModel.Trim();

        // The endpoint decides the format by the name's extension, so the name carries the real one.
        var fileName = "message" + (string.IsNullOrWhiteSpace(item.Extension) ? ".ogg" : "." + item.Extension.TrimStart('.'));

        var text = (await _client
            .TranscribeAsync(settings, bytes, fileName, AiSubject.Media(hash), cancellationToken)
            .ConfigureAwait(false)).Trim();

        Record(item, "transcript", "transcript", "speech", model, "unknown", promptVersion: null, text);

        return text.Length == 0 ? MediaOutcome.NoText : MediaOutcome.Written;
    }

    /// <summary>What each kind of reading applies to, how big a file may be, and what it writes.</summary>
    private static (string[] Kinds, long MaxBytes, string Artifact, string? Prompt) Shape(AiJobKind kind) => kind switch
    {
        // Photos only: a screenshot is exported as a photo, and a sticker or animation is not text.
        AiJobKind.Ocr => (["photo"], MaxImageBytes, "ocr", PromptCatalog.VersionOf(PromptCatalog.OcrImage)),

        // Voice notes and round video messages — speech, one speaker, already attributed. Music
        // files and long videos are left alone.
        AiJobKind.Transcribe => (["voice", "video_message"], MaxAudioBytes, "transcript", null),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of reading."),
    };

    /// <summary>The file's details, and the earliest message it may be read through.</summary>
    private MediaItem? Anchor(string hash, AiJobKind kind)
    {
        var (kinds, maxBytes, _, _) = Shape(kind);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT m.hash, m.media_kind, m.mime, m.extension, m.byte_size,
                   msg.id, msg.thread_id, msg.sender_identity_id, msg.sent_at_unix
            FROM media AS m
            JOIN message_media AS mm ON mm.media_hash = m.hash
            JOIN message AS msg ON msg.id = mm.message_id
            JOIN thread AS t ON t.id = msg.thread_id
            WHERE m.hash = $hash
              AND m.media_kind IN (SELECT value FROM json_each($kinds))
              AND m.byte_size <= $max
              AND {Eligible}
            ORDER BY msg.sent_at_unix, msg.id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$kinds", JsonSerializer.Serialize(kinds));
        command.Parameters.AddWithValue("$max", maxBytes);

        using var reader = command.ExecuteReader();

        return reader.Read()
            ? new MediaItem(
                Hash: reader.GetString(0),
                Kind: reader.GetString(1),
                Mime: reader.IsDBNull(2) ? null : reader.GetString(2),
                Extension: reader.IsDBNull(3) ? null : reader.GetString(3),
                ByteSize: reader.GetInt64(4),
                MessageId: reader.GetInt64(5),
                ThreadId: reader.GetString(6),
                SenderIdentityId: reader.IsDBNull(7) ? null : reader.GetString(7),
                SentAtUnix: reader.GetInt64(8))
            : null;
    }

    private async Task<byte[]> Read(MediaItem item, CancellationToken cancellationToken)
    {
        await using var stream = _media.OpenRead(item.Hash, item.Extension);
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes what was read, and makes it the file's searchable text — in one short transaction.
    /// </summary>
    /// <remarks>
    /// A new model's reading is a new artifact beside the old one, so the two can be compared
    /// (spec §3: "you'll want to diff"), but only the newest is searchable: two transcripts of one
    /// voice note in the results would be the same message found twice.
    /// </remarks>
    private void Record(
        MediaItem item,
        string artifactKind,
        string provenance,
        string engine,
        string model,
        string modelVersion,
        string? promptVersion,
        string text)
    {
        var id = "a_" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{artifactKind}|{item.Hash}|{model}|{promptVersion}")))[..24];

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        Execute(connection, """
            INSERT INTO derived_artifact (
                id, kind, source_media_hash, source_message_id, engine, model, model_version,
                prompt_version, payload_json, input_hash, created_utc)
            VALUES ($id, $kind, $hash, $message, $engine, $model, $modelVersion,
                    $prompt, $payload, $input, $now)
            ON CONFLICT (id) DO UPDATE SET
                payload_json = excluded.payload_json,
                source_message_id = excluded.source_message_id,
                created_utc = excluded.created_utc;
            """,
            ("$id", id), ("$kind", artifactKind), ("$hash", item.Hash), ("$message", item.MessageId),
            ("$engine", engine), ("$model", model), ("$modelVersion", modelVersion),
            ("$prompt", (object?)promptVersion ?? DBNull.Value),
            ("$payload", JsonSerializer.Serialize(new { text })),
            ("$input", $"{model}|{promptVersion}"), ("$now", now));

        // Only the newest reading of a file is searchable.
        Execute(connection, """
            DELETE FROM search_document
            WHERE derived_artifact_id IN (
                SELECT id FROM derived_artifact WHERE source_media_hash = $hash AND kind = $kind);
            """,
            ("$hash", item.Hash), ("$kind", artifactKind));

        if (text.Length > 0)
        {
            Execute(connection, """
                INSERT INTO search_document (
                    derived_artifact_id, provenance, thread_id, sender_identity_id, sent_at_unix, body)
                VALUES ($id, $provenance, $thread, $sender, $sent, $body);
                """,
                ("$id", id), ("$provenance", provenance), ("$thread", item.ThreadId),
                ("$sender", (object?)item.SenderIdentityId ?? DBNull.Value),
                ("$sent", item.SentAtUnix), ("$body", text));
        }

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static string ImageMime(string? extension) =>
        extension?.TrimStart('.').ToLowerInvariant() switch
        {
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            _ => "image/jpeg",
        };
}
