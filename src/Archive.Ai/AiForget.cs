using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Ai;

/// <summary>What the AI layer has produced in a save, as the confirmation shows it.</summary>
public sealed record ForgetCounts(long Facts, long Sessions, long Calls, long Jobs)
{
    public bool IsEmpty => Facts == 0 && Sessions == 0 && Calls == 0 && Jobs == 0;
}

/// <summary>
/// Deletes everything the AI layer produced, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// What makes switching AI on a decision someone can take back, and how they recover from a prompt
/// that produced rubbish (ai-plan.md §11.4). Facts, citations, relationships, sessions, the queue
/// and the statistics go; every message, media file and search index entry stays exactly as it was.
/// </para>
/// <para>
/// Left alone on purpose: the settings, the consent given for an endpoint, and the people the user
/// left out. Those are the user's choices, not the model's output, and forgetting what a model said
/// is no reason to start sending someone's messages again.
/// </para>
/// <para>
/// One transaction, all or nothing. Clearing <c>message.session_id</c> across a large archive holds
/// the writer for a few seconds; that is acceptable for something a person asks for once and
/// confirms by typing, and a half-forgotten archive is not.
/// </para>
/// </remarks>
public sealed class AiForget(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    public ForgetCounts Counts()
    {
        using var connection = _database.Open();

        return Read(connection);
    }

    /// <returns>What was there before it was deleted.</returns>
    public ForgetCounts Everything()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var before = Read(connection);

        using (var command = connection.CreateCommand())
        {
            // Artifacts first: facts and their citations go with them by cascade. Then anything
            // left standing, relationships, the queue, the statistics, and last the sessions —
            // whose removal sets every message's session back to none.
            command.CommandText = """
                DELETE FROM derived_artifact;
                DELETE FROM fact;
                DELETE FROM person_edge;
                DELETE FROM ai_job;
                DELETE FROM ai_interaction;
                DELETE FROM session;
                """;

            command.ExecuteNonQuery();
        }

        transaction.Commit();

        return before;
    }

    private static ForgetCounts Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT (SELECT count(*) FROM fact),
                   (SELECT count(*) FROM session),
                   (SELECT count(*) FROM ai_interaction),
                   (SELECT count(*) FROM ai_job);
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        return new ForgetCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }
}
