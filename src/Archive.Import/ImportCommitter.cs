using System.Globalization;
using System.Text.Json;
using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Import;

/// <summary>
/// Writes normalized messages into a save, idempotently.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency is enforced by the storage layer, not by looking before writing. Every insert is
/// an <c>ON CONFLICT DO NOTHING</c> against a natural key. A read-then-write would double the
/// query count and still race a second importer, whereas the unique index cannot be raced.
/// </para>
/// <para>
/// Writes go through prepared <see cref="SqliteCommand"/>s rather than EF Core: half a million
/// rows through a change tracker is minutes where this is seconds, and none of it needs
/// entity identity or lazy loading.
/// </para>
/// </remarks>
public sealed class ImportCommitter : IDisposable
{
    public const string ImporterVersion = "telegram/1";

    private readonly SqliteConnection _connection;
    private readonly string _importId;
    private readonly string _nowUtc;
    private readonly Dictionary<string, string> _identityIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _threadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _participants = new(StringComparer.Ordinal);

    private SqliteTransaction? _transaction;
    private int _pendingInBatch;

    /// <param name="sourceId">
    /// The source this run belongs to. A newer export of an account already in the archive uses
    /// the same source id as the run that first imported it, which is what makes a re-import
    /// cheap: messages already belonging to the source are not re-linked.
    /// </param>
    public ImportCommitter(
        Database database,
        string platform,
        string sourceId,
        string? sourceLabel,
        string sourcePath,
        string sourceFingerprint,
        int batchSize = 1000)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        BatchSize = batchSize > 0 ? batchSize : 1000;
        Platform = platform;
        SourceId = sourceId;
        _connection = database.Open();
        _importId = Guid.NewGuid().ToString("N");
        _nowUtc = DateTimeOffset.UtcNow.ToString("O");

        Execute("""
            INSERT INTO save_meta (id, owner_is_self, created_utc)
            VALUES (1, 1, $now)
            ON CONFLICT (id) DO NOTHING;
            """, ("$now", _nowUtc));

        // A label is only set when the source is created. Re-running an import must not silently
        // rename a source the user renamed themselves.
        Execute("""
            INSERT INTO import_source (id, platform, label, created_utc)
            VALUES ($id, $platform, $label, $now)
            ON CONFLICT (id) DO NOTHING;
            """,
            ("$id", sourceId),
            ("$platform", platform),
            ("$label", sourceLabel),
            ("$now", _nowUtc));

        Execute("""
            INSERT INTO import (id, source_id, platform, source_path, source_fingerprint,
                                importer_version, status, started_utc)
            VALUES ($id, $source, $platform, $path, $fingerprint, $version, 'running', $started);
            """,
            ("$id", _importId),
            ("$source", sourceId),
            ("$platform", platform),
            ("$path", sourcePath),
            ("$fingerprint", sourceFingerprint),
            ("$version", ImporterVersion),
            ("$started", _nowUtc));

        Begin();
    }

    public string Platform { get; }

    public string SourceId { get; }

    public int BatchSize { get; }

    public string ImportId => _importId;

    public ImportStats Stats { get; } = new();

    /// <summary>
    /// Seeds the owner from the export's personal_information block (§2).
    /// </summary>
    /// <remarks>
    /// If an owner already exists — the normal case from the second import onward — the new
    /// identity is linked to them rather than creating a second owner. The partial unique index
    /// on person would reject a second one anyway; doing it here means the import succeeds
    /// instead of failing on a constraint.
    /// </remarks>
    public void SeedOwner(JsonElement personalInformation)
    {
        var userId = Text(personalInformation, "user_id");

        if (userId is null)
        {
            return;
        }

        var name = string.Join(' ', new[]
        {
            Text(personalInformation, "first_name"),
            Text(personalInformation, "last_name"),
        }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Text(personalInformation, "username") ?? userId;
        }

        var identity = new NormalizedIdentity(
            Platform, userId, Text(personalInformation, "username"), name, IsSynthetic: false);

        var identityId = EnsureIdentity(identity);
        var ownerId = ExistingOwnerId();

        if (ownerId is null)
        {
            ownerId = "owner";
            Execute("""
                INSERT INTO person (id, display_name, is_owner, created_utc)
                VALUES ($id, $name, 1, $now)
                ON CONFLICT (id) DO NOTHING;
                """, ("$id", ownerId), ("$name", name), ("$now", _nowUtc));
        }

        // 'seed' outranks the 'auto' link EnsureIdentity created: this identity is the owner
        // because the export said so, not because the importer guessed.
        Execute("""
            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
            VALUES ($identity, $person, 'seed', $now)
            ON CONFLICT (identity_id) DO UPDATE SET person_id = $person, confidence = 'seed';
            """, ("$identity", identityId), ("$person", ownerId), ("$now", _nowUtc));
    }

    /// <summary>
    /// Deterministic id for a thread, so the same export always produces the same row.
    /// </summary>
    public string ThreadId(string sourceThreadId) => $"{Platform}:{sourceThreadId}";

    public void EnsureThread(string sourceThreadId, string kind, string? title)
    {
        var id = ThreadId(sourceThreadId);

        if (!_threadIds.Add(id))
        {
            return;
        }

        var inserted = Execute("""
            INSERT INTO thread (id, platform, source_thread_id, kind, title, first_import_id, created_utc)
            VALUES ($id, $platform, $source, $kind, $title, $import, $now)
            ON CONFLICT (platform, source_thread_id) DO NOTHING;
            """,
            ("$id", id),
            ("$platform", Platform),
            ("$source", sourceThreadId),
            ("$kind", kind),
            ("$title", title),
            ("$import", _importId),
            ("$now", _nowUtc));

        if (inserted > 0)
        {
            Stats.ThreadsNew++;
        }
    }

    /// <summary>
    /// Inserts an identity if it is new, and returns its id.
    /// </summary>
    /// <remarks>
    /// Ids are derived from the natural key rather than generated, so two imports of the same
    /// export produce byte-identical rows and a database digest can be compared directly.
    /// Synthetic identities — a name with no id (§2) — are keyed by name and flagged, so the
    /// merge UI can present them as the guesses they are.
    /// </remarks>
    public string EnsureIdentity(NormalizedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var id = identity.SourceIdentityId is not null
            ? $"{identity.Platform}:{identity.SourceIdentityId}"
            : $"{identity.Platform}:name:{identity.DisplayName}";

        if (_identityIds.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var inserted = Execute("""
            INSERT INTO identity (id, platform, source_identity_id, handle, display_name, is_synthetic, first_import_id, created_utc)
            VALUES ($id, $platform, $source, $handle, $name, $synthetic, $import, $now)
            ON CONFLICT (id) DO NOTHING;
            """,
            ("$id", id),
            ("$platform", identity.Platform),
            ("$source", identity.SourceIdentityId),
            ("$handle", identity.Handle),
            ("$name", identity.DisplayName),
            ("$synthetic", identity.IsSynthetic ? 1 : 0),
            ("$import", _importId),
            ("$now", _nowUtc));

        if (inserted > 0)
        {
            Stats.IdentitiesNew++;

            // Every identity gets a person immediately, so the archive is browsable by person
            // from the first import. Merging later repoints the link; §1 requires that messages
            // are never rewritten, and this keeps that true.
            var personId = "p:" + id;

            Execute("""
                INSERT INTO person (id, display_name, is_owner, created_utc)
                VALUES ($id, $name, 0, $now)
                ON CONFLICT (id) DO NOTHING;
                """, ("$id", personId), ("$name", identity.DisplayName), ("$now", _nowUtc));

            Execute("""
                INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
                VALUES ($identity, $person, 'auto', $now)
                ON CONFLICT (identity_id) DO NOTHING;
                """, ("$identity", id), ("$person", personId), ("$now", _nowUtc));
        }

        _identityIds[id] = id;
        return id;
    }

    /// <summary>
    /// Commits one message and everything hanging off it.
    /// </summary>
    /// <param name="message">The normalized message.</param>
    /// <param name="resolveMedia">
    /// Produces content hashes for the message's attachments, aligned by index with
    /// <see cref="NormalizedMessage.Media"/>. Null entries mean the file was not stored — either
    /// the export omitted it, or it was referenced but absent from the folder.
    ///
    /// Deliberately a callback, not a list. Storing a file means reading and hashing every byte
    /// of it, and on a re-import almost every message is already known, so resolving eagerly
    /// would re-hash an entire media folder to discover there was nothing to do. It is invoked
    /// only when the message is actually new or its content changed.
    /// </param>
    public void Add(NormalizedMessage message, Func<IReadOnlyList<StoredMedia?>> resolveMedia)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(resolveMedia);

        Stats.MessagesSeen++;

        var threadId = ThreadId(message.SourceThreadId);
        var senderId = message.Sender is null ? null : EnsureIdentity(message.Sender);

        var messageId = InsertMessage(message, threadId, senderId);
        var isFirst = messageId.WasInserted;

        if (isFirst)
        {
            Stats.MessagesInserted++;
            InsertMedia(messageId.Id, message, resolveMedia());
            InsertReactions(messageId.Id, message);
        }
        else
        {
            Stats.MessagesSkipped++;

            // An edit can change attachments too, so a revised message pays the hashing cost.
            // An unchanged one — the overwhelming majority on re-import — does not.
            if (messageId.WasRevised)
            {
                InsertMedia(messageId.Id, message, resolveMedia());
            }
        }

        // Keyed by source, so a re-run of a source the message already belongs to writes nothing.
        // On an unchanged re-import this is an index probe per message rather than a row insert
        // per message — the difference between touching half a million pages and touching none.
        Execute("""
            INSERT INTO message_source (message_id, source_id, first_import_id, seen_utc)
            VALUES ($message, $source, $import, $now)
            ON CONFLICT (message_id, source_id) DO NOTHING;
            """,
            ("$message", messageId.Id),
            ("$source", SourceId),
            ("$import", _importId),
            ("$now", _nowUtc));

        if (senderId is not null && _participants.Add(ParticipantKey(threadId, senderId)))
        {
            Execute("""
                INSERT INTO thread_participant (thread_id, identity_id, first_seen_unix)
                VALUES ($thread, $identity, $unix)
                ON CONFLICT (thread_id, identity_id) DO NOTHING;
                """,
                ("$thread", threadId),
                ("$identity", senderId),
                ("$unix", message.SentAtUnix));
        }

        if (++_pendingInBatch >= BatchSize)
        {
            Checkpoint();
        }
    }

    /// <summary>Flushes the current batch. Reads are never blocked for longer than one batch.</summary>
    public void Checkpoint()
    {
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
        _pendingInBatch = 0;
        Begin();
    }

    public void Complete()
    {
        Checkpoint();

        Execute("""
            UPDATE import
            SET status = 'completed', finished_utc = $finished, stats_json = $stats
            WHERE id = $id;
            """,
            ("$finished", DateTimeOffset.UtcNow.ToString("O")),
            ("$stats", JsonSerializer.Serialize(Stats)),
            ("$id", _importId));

        Checkpoint();

        // FTS5 leaves many small b-tree segments after a bulk insert; merging them keeps the
        // first search after an import from paying for the whole import.
        Execute("INSERT INTO search_fts (search_fts) VALUES ('optimize');");
        Execute("ANALYZE;");
    }

    public void Fail(string error)
    {
        Checkpoint();

        Execute("""
            UPDATE import SET status = 'failed', finished_utc = $finished, last_error = $error, stats_json = $stats
            WHERE id = $id;
            """,
            ("$finished", DateTimeOffset.UtcNow.ToString("O")),
            ("$error", error),
            ("$stats", JsonSerializer.Serialize(Stats)),
            ("$id", _importId));

        Checkpoint();
    }

    private readonly record struct InsertedMessage(long Id, bool WasInserted, bool WasRevised = false);

    private InsertedMessage InsertMessage(NormalizedMessage message, string threadId, string? senderId)
    {
        using var command = NewCommand("""
            INSERT INTO message (uid, thread_id, sender_identity_id, kind, service_action, sent_at_utc,
                                 sent_at_unix, tz_offset_minutes, plaintext, entities_json, content_hash,
                                 reply_to_uid, forwarded_from, forwarded_at_utc, via_bot, edited_at_utc,
                                 raw_json, first_import_id, importer_version)
            VALUES ($uid, $thread, $sender, $kind, $action, $sentUtc, $sentUnix, $tz, $plaintext,
                    $entities, $hash, $reply, $forwardedFrom, $forwardedAt, $viaBot, $edited,
                    $raw, $import, $version)
            ON CONFLICT (uid) DO NOTHING
            RETURNING id;
            """);

        Bind(command,
            ("$uid", message.Uid),
            ("$thread", threadId),
            ("$sender", senderId),
            ("$kind", message.Kind),
            ("$action", message.ServiceAction),
            ("$sentUtc", message.SentAtUtc),
            ("$sentUnix", message.SentAtUnix),
            ("$tz", message.TzOffsetMinutes),
            ("$plaintext", message.Plaintext),
            ("$entities", message.EntitiesJson),
            ("$hash", message.ContentHash),
            ("$reply", message.ReplyToUid),
            ("$forwardedFrom", message.ForwardedFrom),
            ("$forwardedAt", message.ForwardedAtUtc),
            ("$viaBot", message.ViaBot),
            ("$edited", message.EditedAtUtc),
            ("$raw", message.RawJson),
            ("$import", _importId),
            ("$version", ImporterVersion));

        var result = command.ExecuteScalar();

        if (result is not null and not DBNull)
        {
            return new InsertedMessage(Convert.ToInt64(result, CultureInfo.InvariantCulture), WasInserted: true);
        }

        return ReconcileExisting(message);
    }

    /// <summary>
    /// Handles a message that is already stored: usually a no-op, occasionally an edit.
    /// </summary>
    /// <remarks>
    /// When the body differs, the superseded content is written to message_revision before the
    /// message is updated, so §1's rule that nothing imported is ever lost holds even though the
    /// visible row carries the latest text. observed_import_id on the revision is the import that
    /// superseded the content, which is the only import we can name with certainty.
    /// </remarks>
    private InsertedMessage ReconcileExisting(NormalizedMessage message)
    {
        using var select = NewCommand(
            "SELECT id, content_hash, plaintext, entities_json, raw_json FROM message WHERE uid = $uid;");
        Bind(select, ("$uid", message.Uid));

        long id;
        string existingHash;
        string existingText;
        object existingEntities;
        object existingRaw;

        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Message '{message.Uid}' vanished mid-import.");
            }

            id = reader.GetInt64(0);
            existingHash = reader.GetString(1);
            existingText = reader.GetString(2);
            existingEntities = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
            existingRaw = reader.IsDBNull(4) ? DBNull.Value : reader.GetString(4);
        }

        if (string.Equals(existingHash, message.ContentHash, StringComparison.Ordinal))
        {
            return new InsertedMessage(id, WasInserted: false);
        }

        Execute("""
            INSERT INTO message_revision (message_id, observed_import_id, plaintext, entities_json,
                                          content_hash, raw_json, observed_utc)
            VALUES ($message, $import, $plaintext, $entities, $hash, $raw, $now);
            """,
            ("$message", id),
            ("$import", _importId),
            ("$plaintext", existingText),
            ("$entities", existingEntities),
            ("$hash", existingHash),
            ("$raw", existingRaw),
            ("$now", _nowUtc));

        Execute("""
            UPDATE message
            SET plaintext = $plaintext, entities_json = $entities, content_hash = $hash,
                edited_at_utc = $edited, raw_json = $raw
            WHERE id = $id;
            """,
            ("$plaintext", message.Plaintext),
            ("$entities", message.EntitiesJson),
            ("$hash", message.ContentHash),
            ("$edited", message.EditedAtUtc),
            ("$raw", message.RawJson),
            ("$id", id));

        Stats.MessagesRevised++;
        return new InsertedMessage(id, WasInserted: false, WasRevised: true);
    }

    private void InsertMedia(long messageId, NormalizedMessage message, IReadOnlyList<StoredMedia?> stored)
    {
        for (var ordinal = 0; ordinal < message.Media.Count; ordinal++)
        {
            var media = message.Media[ordinal];
            var file = ordinal < stored.Count ? stored[ordinal] : null;

            if (file is { } present)
            {
                Execute("""
                    INSERT INTO media (hash, byte_size, mime, extension, media_kind, width, height,
                                       duration_seconds, first_import_id, created_utc)
                    VALUES ($hash, $size, $mime, $extension, $kind, $width, $height, $duration, $import, $now)
                    ON CONFLICT (hash) DO NOTHING;
                    """,
                    ("$hash", present.Hash),
                    ("$size", present.ByteSize),
                    ("$mime", media.Mime),
                    ("$extension", present.Extension),
                    ("$kind", media.MediaKind),
                    ("$width", media.Width),
                    ("$height", media.Height),
                    ("$duration", media.DurationSeconds),
                    ("$import", _importId),
                    ("$now", _nowUtc));
            }

            Execute("""
                INSERT INTO message_media (message_id, ordinal, media_hash, export_path, original_filename,
                                           missing_reason, sticker_emoji)
                VALUES ($message, $ordinal, $hash, $path, $filename, $missing, $emoji)
                ON CONFLICT (message_id, ordinal) DO NOTHING;
                """,
                ("$message", messageId),
                ("$ordinal", ordinal),
                ("$hash", file?.Hash),
                ("$path", media.ExportPath),
                ("$filename", media.OriginalFilename),
                ("$missing", media.MissingReason ?? (file is null ? "not found in export folder" : null)),
                ("$emoji", media.StickerEmoji));
        }
    }

    private void InsertReactions(long messageId, NormalizedMessage message)
    {
        foreach (var reaction in message.Reactions)
        {
            var actorId = reaction.ActorIdentity is null ? null : EnsureIdentity(reaction.ActorIdentity);

            Execute("""
                INSERT INTO reaction (message_id, emoji, custom_emoji_id, actor_identity_id, count, reacted_at_utc)
                VALUES ($message, $emoji, $custom, $actor, $count, $at)
                ON CONFLICT (message_id, emoji, ifnull(actor_identity_id, '')) DO NOTHING;
                """,
                ("$message", messageId),
                ("$emoji", reaction.Emoji),
                ("$custom", reaction.CustomEmojiId),
                ("$actor", actorId),
                ("$count", reaction.Count),
                ("$at", reaction.ReactedAtUtc));
        }
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private string? ExistingOwnerId()
    {
        using var command = NewCommand("SELECT id FROM person WHERE is_owner = 1 LIMIT 1;");
        return command.ExecuteScalar() as string;
    }

    /// <summary>Composite key for the participant cache; the unit separator cannot occur in an id.</summary>
    private static string ParticipantKey(string threadId, string identityId) =>
        threadId + (char)31 + identityId;

    private void Begin() => _transaction = _connection.BeginTransaction();

    private SqliteCommand NewCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        return command;
    }

    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = NewCommand(sql);
        Bind(command, parameters);
        return command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _connection.Dispose();
    }
}

/// <summary>A file that made it into the media store.</summary>
public readonly record struct StoredMedia(string Hash, string Extension, long ByteSize);
