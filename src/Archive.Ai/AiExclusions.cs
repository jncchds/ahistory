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
            using var revive = connection.CreateCommand();

            revive.CommandText = """
                UPDATE ai_job
                SET state = 'pending', attempts = 0, lease_utc = NULL, updated_utc = $now
                WHERE kind = 'extract'
                  AND state = 'done'
                  AND subject_id IN (
                      SELECT s.id
                      FROM session AS s
                      JOIN thread_participant AS tp ON tp.thread_id = s.thread_id
                      JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
                      WHERE ip.person_id = $person)
                  AND NOT EXISTS (
                      SELECT 1 FROM derived_artifact AS d
                      WHERE d.source_session_id = ai_job.subject_id AND d.kind = 'session_extract');
                """;

            revive.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            revive.Parameters.AddWithValue("$person", personId);
            revive.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
