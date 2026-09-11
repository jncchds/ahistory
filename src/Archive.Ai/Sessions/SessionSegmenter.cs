using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Ai.Sessions;

/// <summary>What segmenting one thread did.</summary>
public sealed record SegmentationResult(int Sessions, int Substantive, int Added, int Removed)
{
    public static SegmentationResult Nothing { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Splits a thread into sessions on gaps of silence (§6.1).
/// </summary>
/// <remarks>
/// <para>
/// The spec calls this the most important step, and the reason is worth repeating: fixed-size
/// chunks cut through the middle of exchanges and extraction quality collapses. A session is what
/// two people would call one conversation — bounded by silence, typically 10–200 messages — and it
/// is the unit for extraction, embedding and retrieval alike.
/// </para>
/// <para>
/// Sessions belong to a <b>thread</b>, not to a person. The per-person view is a union across
/// threads and composes sessions rather than defining them; segmenting per person would give the
/// same group conversation different boundaries for each participant, and then every cache key
/// derived from it would disagree with itself.
/// </para>
/// <para>
/// No model is involved, which is what makes this the right place to build the queue: pause,
/// resume, crash recovery and progress all get exercised before a single call costs anything.
/// </para>
/// </remarks>
public sealed class SessionSegmenter(Database database, TimeSpan? gap = null)
{
    /// <summary>
    /// Bump this and every session in every save is recomputed, along with everything keyed to one.
    /// </summary>
    public const string Version = "1";

    /// <summary>
    /// How much silence ends a conversation.
    /// </summary>
    /// <remarks>
    /// Four hours: long enough that a pause for a meeting or a meal does not split an exchange in
    /// half, short enough that this morning and last night are not one session. Fixed for now, and
    /// it should not stay that way — the same threshold is wrong for daily correspondents and for
    /// people who write twice a year, and eventually it has to be relative to a pair's own cadence.
    /// </remarks>
    public static readonly TimeSpan DefaultGap = TimeSpan.FromHours(4);

    /// <summary>How many sessions are written per transaction.</summary>
    /// <remarks>
    /// SQLite has one writer. A thread with a hundred thousand messages committed in one go would
    /// hold it for the duration and stall every reader in the app, which is the thing P1 forbids
    /// most specifically. Segmentation is computed outside any transaction and committed in
    /// batches, so a partially segmented thread is a normal, resumable state.
    /// </remarks>
    private const int BatchSize = 100;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly long _gapSeconds = (long)(gap ?? DefaultGap).TotalSeconds;

    /// <summary>Every thread in the save, newest activity first.</summary>
    /// <remarks>
    /// The order the runner works in. A ten-year archive segmented oldest-first shows nothing
    /// recognisable for a long time; starting with what was said recently means the first thing a
    /// user sees is a conversation they remember.
    /// </remarks>
    public IReadOnlyList<(string ThreadId, long LastUnix, long Messages)> Threads()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT thread_id, max(sent_at_unix) AS last_unix, count(*) AS messages
            FROM message
            WHERE kind = 'message' AND is_deleted = 0
            GROUP BY thread_id
            ORDER BY last_unix DESC;
            """;

        var threads = new List<(string, long, long)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            threads.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return threads;
    }

    /// <summary>
    /// What the thread's messages currently hash to.
    /// </summary>
    /// <remarks>
    /// The job's <c>input_hash</c>: count, last timestamp and the two versions. Cheap to compute
    /// for every thread and enough to answer "has anything changed since this was last done" —
    /// which is what keeps a re-import from re-segmenting a decade (§12).
    /// </remarks>
    public string InputHash(string threadId, long lastUnix, long messages) =>
        Hash($"{threadId}|{lastUnix}|{messages}|{Version}|{SessionFilter.Version}");

    /// <summary>
    /// Segments one thread, leaving it with exactly the sessions its messages imply.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction: a session's identity is derived from the messages in it, so
    /// running this twice computes the same ids and writes nothing the second time. A session
    /// whose membership changed gets a new identity and the old one is retired — the model's
    /// conclusions about it stop being believed, and anything the user corrected is kept (see
    /// <see cref="Remove"/>).
    /// </remarks>
    public SegmentationResult SegmentThread(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var planned = Plan(threadId);

        PruneOtherVersions(threadId, cancellationToken);

        var existing = ExistingSessions(threadId);

        var wanted = planned.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var removed = existing.Where(id => !wanted.ContainsKey(id)).ToList();
        var added = planned.Where(s => !existing.Contains(s.Id)).ToList();

        Remove(removed, cancellationToken);
        Insert(added, cancellationToken);

        return new SegmentationResult(
            Sessions: planned.Count,
            Substantive: planned.Count(s => s.IsSubstantive),
            Added: added.Count,
            Removed: removed.Count);
    }

    /// <summary>One session as it will be written, with the messages that belong to it.</summary>
    private sealed record PlannedSession(
        string Id,
        string ThreadId,
        long StartedAtUnix,
        long EndedAtUnix,
        string MemberHash,
        bool IsSubstantive,
        IReadOnlyList<long> MessageIds);

    /// <summary>
    /// Works out the sessions a thread's messages imply, touching nothing.
    /// </summary>
    /// <remarks>
    /// Service messages and deleted ones are left out. "X joined the group" is not something
    /// anybody said, and letting one anchor a session would put a boundary where the conversation
    /// did not have one.
    /// </remarks>
    private List<PlannedSession> Plan(string threadId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, uid, sent_at_unix, plaintext
            FROM message
            WHERE thread_id = $thread AND kind = 'message' AND is_deleted = 0
            ORDER BY sent_at_unix, id;
            """;

        command.Parameters.AddWithValue("$thread", threadId);

        var sessions = new List<PlannedSession>();

        var ids = new List<long>();
        var uids = new List<string>();
        var texts = new List<string>();

        long startedAt = 0;
        long previous = 0;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var uid = reader.GetString(1);
            var sentAt = reader.GetInt64(2);
            var text = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

            if (ids.Count > 0 && sentAt - previous >= _gapSeconds)
            {
                sessions.Add(Build(threadId, startedAt, previous, ids, uids, texts));

                ids = [];
                uids = [];
                texts = [];
            }

            if (ids.Count == 0)
            {
                startedAt = sentAt;
            }

            ids.Add(id);
            uids.Add(uid);
            texts.Add(text);
            previous = sentAt;
        }

        if (ids.Count > 0)
        {
            sessions.Add(Build(threadId, startedAt, previous, ids, uids, texts));
        }

        return sessions;
    }

    private static PlannedSession Build(
        string threadId,
        long startedAt,
        long endedAt,
        List<long> ids,
        List<string> uids,
        List<string> texts)
    {
        // Hashed over uids rather than row ids: a uid is stable across re-import and a row id is
        // not guaranteed to be, and this hash is the cache key every derived artifact hangs off.
        var memberHash = Hash(string.Join('\n', uids));

        return new PlannedSession(
            Id: "s_" + Hash(threadId + "|" + memberHash)[..24],
            ThreadId: threadId,
            StartedAtUnix: startedAt,
            EndedAtUnix: endedAt,
            MemberHash: memberHash,
            IsSubstantive: SessionFilter.IsSubstantive(texts),
            MessageIds: ids);
    }

    private HashSet<string> ExistingSessions(string threadId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT id FROM session WHERE thread_id = $thread AND segmenter_version = $version;";

        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$version", Version);

        var ids = new HashSet<string>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// Drops sessions left by a different segmenter version.
    /// </summary>
    /// <remarks>
    /// They are not sessions to keep or to compare against — the boundaries they express are the
    /// old rules' answer, and everything derived from one is keyed to a membership that no longer
    /// describes anything. They are retired like any other session that stopped existing.
    /// </remarks>
    private void PruneOtherVersions(string threadId, CancellationToken cancellationToken)
    {
        List<string> stale;

        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id FROM session WHERE thread_id = $thread AND segmenter_version <> $version;";

            command.Parameters.AddWithValue("$thread", threadId);
            command.Parameters.AddWithValue("$version", Version);

            stale = [];

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                stale.Add(reader.GetString(0));
            }
        }

        Remove(stale, cancellationToken);
    }

    /// <summary>
    /// Retires sessions that no longer exist, without losing what a person said about them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deleting a session row outright would be a data-loss bug with a respectable-looking cause.
    /// <c>derived_artifact.source_session_id</c> cascades, and so does <c>fact.derived_artifact_id</c>
    /// — so a thread gaining one message would re-segment, delete the session it landed in, and
    /// take with it every fact extracted from that session, <b>including the ones the user
    /// corrected</b>. That is precisely the outcome <c>fact.source</c> exists to prevent.
    /// </para>
    /// <para>
    /// So, in the same short transaction: the model's conclusions from the old session stop being
    /// believed (assertion time — they were believed, and a diary narrating the past must still be
    /// able to say so); the artifacts are detached from the session rather than cascaded away; and
    /// only then is the session row removed. A user's edit or deletion is untouched, and the new
    /// session that replaced this one is read afresh.
    /// </para>
    /// </remarks>
    private void Remove(List<string> ids, CancellationToken cancellationToken)
    {
        foreach (var batch in ids.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var id in batch)
            {
                using var retract = connection.CreateCommand();

                retract.CommandText = """
                    UPDATE fact
                    SET retracted_utc = $now
                    WHERE retracted_utc IS NULL
                      AND source = 'extracted'
                      AND derived_artifact_id IN (
                          SELECT id FROM derived_artifact WHERE source_session_id = $id);
                    """;

                retract.Parameters.AddWithValue("$now", now);
                retract.Parameters.AddWithValue("$id", id);
                retract.ExecuteNonQuery();

                using var detach = connection.CreateCommand();

                detach.CommandText =
                    "UPDATE derived_artifact SET source_session_id = NULL WHERE source_session_id = $id;";

                detach.Parameters.AddWithValue("$id", id);
                detach.ExecuteNonQuery();

                using var delete = connection.CreateCommand();

                delete.CommandText = "DELETE FROM session WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private void Insert(List<PlannedSession> sessions, CancellationToken cancellationToken)
    {
        foreach (var batch in sessions.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var session in batch)
            {
                InsertOne(connection, session);
                Assign(connection, session);
            }

            transaction.Commit();
        }
    }

    private static void InsertOne(SqliteConnection connection, PlannedSession session)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO session (
                id, thread_id, started_at_unix, ended_at_unix, message_count,
                member_hash, segmenter_version, is_substantive, filter_version)
            VALUES ($id, $thread, $started, $ended, $count, $hash, $version, $substantive, $filter)
            ON CONFLICT (id) DO UPDATE SET
                is_substantive = excluded.is_substantive,
                filter_version = excluded.filter_version;
            """;

        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$thread", session.ThreadId);
        command.Parameters.AddWithValue("$started", session.StartedAtUnix);
        command.Parameters.AddWithValue("$ended", session.EndedAtUnix);
        command.Parameters.AddWithValue("$count", session.MessageIds.Count);
        command.Parameters.AddWithValue("$hash", session.MemberHash);
        command.Parameters.AddWithValue("$version", Version);
        command.Parameters.AddWithValue("$substantive", session.IsSubstantive ? 1 : 0);
        command.Parameters.AddWithValue("$filter", SessionFilter.Version);

        command.ExecuteNonQuery();
    }

    private static void Assign(SqliteConnection connection, PlannedSession session)
    {
        using var command = connection.CreateCommand();

        // Guarded by the comparison so that re-running writes no pages at all for a thread that
        // has not changed — which is what makes segmenting the whole archive on every start cheap
        // enough to be the normal thing to do.
        command.CommandText = """
            UPDATE message
            SET session_id = $session
            WHERE id = $id AND (session_id IS NULL OR session_id <> $session);
            """;

        command.Parameters.AddWithValue("$session", session.Id);

        var idParameter = command.Parameters.AddWithValue("$id", 0L);

        foreach (var id in session.MessageIds)
        {
            idParameter.Value = id;
            command.ExecuteNonQuery();
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Formats a count for a progress line, invariantly.</summary>
    internal static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
