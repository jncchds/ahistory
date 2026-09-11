using Archive.Data;

namespace Archive.Sync;

/// <summary>One chat of a connected account, and what the user decided about it.</summary>
/// <param name="Decision">
/// <c>include</c>, <c>ignore</c>, or null for a chat nobody has decided about yet. Null is not
/// "ignore": it is listed to be decided, and including it later reads its history from the start.
/// </param>
/// <param name="MessageCount">How much of it is already in the archive, from any source.</param>
public sealed record SyncChat(
    string ChatId,
    string PeerKind,
    string ThreadKind,
    string? Title,
    string? Decision,
    long? LastMessageUnix,
    long MessageCount)
{
    public bool IsIncluded => Decision == "include";

    public bool IsUndecided => Decision is null;
}

/// <summary>
/// The save's side of a connected account: which chats it contributes, and how far each was read.
/// </summary>
/// <remarks>
/// In the save rather than in <c>sync.json</c>, because these are decisions about the archive
/// rather than about the machine: the same save opened on a laptop keeps them, and a new machine
/// does not have to be told again which channels to leave out.
/// </remarks>
public sealed class SyncStore(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Every chat known for a source, most recently active first.</summary>
    public IReadOnlyList<SyncChat> Chats(string sourceId, string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        // The message count comes from the thread this chat lands in, which is keyed the same way
        // an import keys it — so a chat already imported from an export shows what is there before
        // it is ever read from the account.
        command.CommandText = """
            SELECT c.chat_id, c.peer_kind, c.thread_kind, c.title, c.decision, c.last_message_unix,
                   (SELECT count(*) FROM message m WHERE m.thread_id = $platform || ':' || c.chat_id)
            FROM sync_chat c
            WHERE c.source_id = $source
            ORDER BY c.last_message_unix DESC, c.title;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$platform", platform);

        var chats = new List<SyncChat>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            chats.Add(new SyncChat(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return chats;
    }

    /// <summary>
    /// Records the chats an account has, without touching what the user decided about them.
    /// </summary>
    /// <remarks>
    /// Titles and last-message times are refreshed on every listing — a group gets renamed — but
    /// the decision is the user's and is never written here. A chat that disappears from the
    /// listing keeps its row: leaving a group does not unsay the decision to keep its history.
    /// </remarks>
    public void RecordChats(string sourceId, IEnumerable<RemoteChat> chats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(chats);

        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var chat in chats)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sync_chat (source_id, chat_id, peer_kind, thread_kind, title,
                                       last_message_unix, discovered_utc)
                VALUES ($source, $chat, $peer, $kind, $title, $last, $now)
                ON CONFLICT (source_id, chat_id) DO UPDATE SET
                    peer_kind = $peer,
                    thread_kind = $kind,
                    title = $title,
                    last_message_unix = max(ifnull(last_message_unix, 0), ifnull($last, 0));
                """;
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$chat", chat.ChatId);
            command.Parameters.AddWithValue("$peer", chat.PeerKind);
            command.Parameters.AddWithValue("$kind", chat.ThreadKind);
            command.Parameters.AddWithValue("$title", (object?)chat.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$last", (object?)chat.LastMessageUnix ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Includes or ignores one chat. Null puts it back to undecided.</summary>
    public void Decide(string sourceId, string chatId, string? decision)
    {
        if (decision is not (null or "include" or "ignore"))
        {
            throw new ArgumentException($"A chat is included, ignored or undecided, not '{decision}'.", nameof(decision));
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_chat
            SET decision = $decision, decided_utc = $now
            WHERE source_id = $source AND chat_id = $chat;
            """;
        command.Parameters.AddWithValue("$decision", (object?)decision ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", decision is null ? DBNull.Value : DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$chat", chatId);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Decides every chat of a kind at once — "all my direct chats", "none of the channels".
    /// </summary>
    /// <param name="onlyUndecided">
    /// True to leave chats the user has already decided about alone, which is what a bulk button
    /// should do: it must not quietly reverse a per-chat answer given earlier.
    /// </param>
    public int DecideKind(string sourceId, string threadKind, string decision, bool onlyUndecided = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decision);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE sync_chat
            SET decision = $decision, decided_utc = $now
            WHERE source_id = $source AND thread_kind = $kind
              {(onlyUndecided ? "AND decision IS NULL" : string.Empty)};
            """;
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$kind", threadKind);

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// The source a connected account of this platform writes into, if one has ever been listed.
    /// </summary>
    /// <remarks>
    /// A save can hold several sources for a platform — an export someone handed over, and your own
    /// account — so this names the one a connection is behind: the one with chats listed against it.
    /// </remarks>
    public string? SourceFor(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.source_id
            FROM sync_chat c
            JOIN import_source s ON s.id = c.source_id
            WHERE s.platform = $platform
            GROUP BY c.source_id
            ORDER BY count(*) DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$platform", platform);

        return command.ExecuteScalar() as string;
    }

    /// <summary>How far a scope has been read, or null if it never has.</summary>
    public string? Cursor(string sourceId, string scope)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor FROM sync_state WHERE source_id = $source AND scope = $scope;";
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$scope", scope);

        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Which of these messages the archive already has.
    /// </summary>
    /// <remarks>
    /// Asked once per page, before anything is downloaded: an attachment on a message that is
    /// already stored is a download nobody needs, and on a re-read of a long chat that is almost
    /// all of them.
    /// </remarks>
    public HashSet<string> KnownUids(IReadOnlyCollection<string> uids)
    {
        ArgumentNullException.ThrowIfNull(uids);

        var known = new HashSet<string>(StringComparer.Ordinal);

        if (uids.Count == 0)
        {
            return known;
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        var names = uids.Select((_, i) => "$u" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        command.CommandText = $"SELECT uid FROM message WHERE uid IN ({string.Join(", ", names)});";

        var index = 0;

        foreach (var uid in uids)
        {
            command.Parameters.AddWithValue(names[index++], uid);
        }

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            known.Add(reader.GetString(0));
        }

        return known;
    }

    /// <summary>
    /// The chats a deletion could refer to, for a platform that reports deletions by message id
    /// alone.
    /// </summary>
    /// <remarks>
    /// Telegram numbers messages in private chats and basic groups per account rather than per
    /// chat, so <c>UpdateDeleteMessages</c> carries ids and no peer. The uid to mark is whichever
    /// of these chats holds that id — a handful of exact lookups rather than a scan.
    /// </remarks>
    public IReadOnlyList<string> IncludedChatIds(string sourceId, params string[] peerKinds)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        var filter = peerKinds.Length == 0
            ? string.Empty
            : " AND peer_kind IN (" + string.Join(", ", peerKinds.Select((_, i) => "$k" + i)) + ")";

        command.CommandText = $"""
            SELECT chat_id FROM sync_chat
            WHERE source_id = $source AND decision = 'include'{filter};
            """;
        command.Parameters.AddWithValue("$source", sourceId);

        for (var i = 0; i < peerKinds.Length; i++)
        {
            command.Parameters.AddWithValue("$k" + i, peerKinds[i]);
        }

        var ids = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>Forgets how far everything has been read, so the next run reads from scratch.</summary>
    /// <remarks>
    /// Safe by construction — every message is keyed by uid, so re-reading writes nothing new —
    /// and the way out of a cursor that has ended up ahead of the archive.
    /// </remarks>
    public void ForgetCursors(string sourceId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_state WHERE source_id = $source;";
        command.Parameters.AddWithValue("$source", sourceId);

        command.ExecuteNonQuery();
    }
}
