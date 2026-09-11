using System.Globalization;
using Archive.Data;

namespace Archive.Ai;

/// <summary>
/// People whose correspondence is not to be read by a model at all.
/// </summary>
/// <remarks>
/// <para>
/// Some correspondence should not be profiled, and with a hosted endpoint "not analysed" and "not
/// sent" have to be the same promise (ai-plan.md §11.3). So leaving a person out means: their direct
/// conversations are never queued or read, and their lines are dropped from any group transcript
/// before it goes anywhere. What they wrote does not leave the machine.
/// </para>
/// <para>
/// A group they were in is still read without them, because leaving one person out of a
/// fifty-person group should not silence the other forty-nine.
/// </para>
/// </remarks>
public sealed class AiExclusions(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Whether a whole conversation is left out.</summary>
    public bool IsThreadExcluded(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT ai_excluded FROM thread WHERE id = $thread;";
        command.Parameters.AddWithValue("$thread", threadId);

        return command.ExecuteScalar() is long flag && flag == 1;
    }

    /// <summary>
    /// Leaves a whole conversation out, or lets it back in.
    /// </summary>
    /// <remarks>
    /// For the room rather than the person: a group that is nobody's business, or a direct thread
    /// with someone whose other conversations are fine to read. Letting it back in re-queues what was
    /// skipped, for the same reason as <see cref="Set"/>.
    /// </remarks>
    public void SetThread(string threadId, bool excluded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var flag = connection.CreateCommand())
        {
            flag.CommandText = "UPDATE thread SET ai_excluded = $excluded WHERE id = $thread;";
            flag.Parameters.AddWithValue("$excluded", excluded ? 1 : 0);
            flag.Parameters.AddWithValue("$thread", threadId);
            flag.ExecuteNonQuery();
        }

        if (!excluded)
        {
            Revive(connection, "SELECT id FROM session WHERE thread_id = $subject", threadId);
        }

        transaction.Commit();
    }

    /// <summary>Whether this save has said no to being read by a model at all.</summary>
    public bool SaveOptedOut()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT coalesce(max(ai_opt_out), 0) FROM save_meta;";

        return command.ExecuteScalar() is long flag && flag == 1;
    }

    /// <summary>
    /// Says, for this save, that nothing in it is to be read by a model — on any machine.
    /// </summary>
    /// <remarks>
    /// §9: a save built from someone else's archive should not start being profiled because the
    /// machine it was opened on has AI switched on. Enabling AI is a machine setting; this is the
    /// save's own answer, it travels with the file, and it wins.
    /// </remarks>
    public void SetSaveOptOut(bool optedOut)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var flag = connection.CreateCommand())
        {
            flag.CommandText = """
                INSERT INTO save_meta (id, created_utc, ai_opt_out) VALUES (1, $now, $out)
                ON CONFLICT (id) DO UPDATE SET ai_opt_out = excluded.ai_opt_out;
                """;
            flag.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            flag.Parameters.AddWithValue("$out", optedOut ? 1 : 0);
            flag.ExecuteNonQuery();
        }

        if (!optedOut)
        {
            Revive(connection, "SELECT id FROM session WHERE $subject IS NOT NULL", "all");
        }

        transaction.Commit();
    }

    public bool IsExcluded(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT ai_excluded FROM person WHERE id = $person;";
        command.Parameters.AddWithValue("$person", personId);

        return command.ExecuteScalar() is long flag && flag == 1;
    }

    /// <summary>
    /// Leaves a person out, or lets them back in.
    /// </summary>
    /// <remarks>
    /// Letting someone back in puts their skipped conversations back in the queue. Without that,
    /// a session skipped while they were left out would stay marked done with nothing read, and
    /// asking for the work again would find a job it already had — so it would never be read at all.
    /// A group conversation read without them is not re-read on their return; a prompt change will.
    /// </remarks>
    public void Set(string personId, bool excluded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var flag = connection.CreateCommand())
        {
            flag.CommandText = "UPDATE person SET ai_excluded = $excluded WHERE id = $person;";
            flag.Parameters.AddWithValue("$excluded", excluded ? 1 : 0);
            flag.Parameters.AddWithValue("$person", personId);
            flag.ExecuteNonQuery();
        }

        if (!excluded)
        {
            Revive(
                connection,
                """
                SELECT s.id
                FROM session AS s
                JOIN thread_participant AS tp ON tp.thread_id = s.thread_id
                JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
                WHERE ip.person_id = $subject
                """,
                personId);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Puts back in the queue the reading that was skipped while something was left out.
    /// </summary>
    /// <remarks>
    /// A skipped session is marked done with nothing read. Without this, asking for the work again
    /// would find the job it already has, and the conversation would never be read at all.
    /// </remarks>
    /// <param name="sessions">A query for the session ids concerned, taking <c>$subject</c>.</param>
    private static void Revive(Microsoft.Data.Sqlite.SqliteConnection connection, string sessions, string subject)
    {
        using var revive = connection.CreateCommand();

        revive.CommandText = $"""
            UPDATE ai_job
            SET state = 'pending', attempts = 0, lease_utc = NULL, updated_utc = $now
            WHERE kind = 'extract'
              AND state = 'done'
              AND subject_id IN ({sessions})
              AND NOT EXISTS (
                  SELECT 1 FROM derived_artifact AS d
                  WHERE d.source_session_id = ai_job.subject_id AND d.kind = 'session_extract');
            """;

        revive.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        revive.Parameters.AddWithValue("$subject", subject);
        revive.ExecuteNonQuery();
    }
}
