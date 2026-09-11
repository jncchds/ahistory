using System.Globalization;
using Archive.Data;

namespace Archive.Ai.Jobs;

/// <summary>What a job does.</summary>
public enum AiJobKind
{
    /// <summary>Split a thread into sessions and classify each (§6.1, §6.2). No model involved.</summary>
    Segment,

    Extract,
    Adjudicate,
    Rollup,
    Diary,
    Embed,

    /// <summary>Speech in a voice or video message to text (A7).</summary>
    Transcribe,

    /// <summary>The text in an image, copied out (A7).</summary>
    Ocr,
}

/// <summary>Where a job has got to.</summary>
public enum AiJobState
{
    Pending,

    /// <summary>Claimed by a runner, with a lease that expires if the app dies holding it.</summary>
    Running,

    Done,

    /// <summary>Failed enough times to stop retrying. Visible, not swallowed.</summary>
    Failed,

    /// <summary>Completed, but produced something a person should look at (§5.4).</summary>
    NeedsReview,
}

/// <summary>One unit of work.</summary>
public sealed record AiJob(
    long Id,
    AiJobKind Kind,
    string SubjectKind,
    string SubjectId,
    AiJobState State,
    int Priority,
    int Attempts,
    string? InputHash);

/// <summary>How much work there is, by state.</summary>
public sealed record AiJobCounts(long Pending, long Running, long Done, long Failed, long NeedsReview)
{
    public long Total => Pending + Running + Done + Failed + NeedsReview;

    public static AiJobCounts Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// The queue: what there is to do, what is being done, and what went wrong.
/// </summary>
/// <remarks>
/// <para>
/// Every write here is one short statement on its own connection. The queue is read and written
/// while the user is reading their archive, and SQLite has a single writer — a queue operation
/// that held a transaction open would stall the app it exists to stay out of the way of.
/// </para>
/// <para>
/// Claiming is a conditional UPDATE rather than a select-then-update, so two runners cannot take
/// the same job: the one whose UPDATE affects a row has it, and the other tries again.
/// </para>
/// </remarks>
public sealed class AiJobs(Database database)
{
    /// <summary>
    /// How long a claim on a job is good for.
    /// </summary>
    /// <remarks>
    /// A job still running past this is assumed dead and reclaimed, so the answer has to be longer
    /// than the slowest legitimate unit of work — a model call against a large session on a busy
    /// local box — and short enough that a crash does not leave work stranded for an afternoon.
    /// </remarks>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>After this many failures a job stops being retried and starts being visible.</summary>
    public const int MaxAttempts = 3;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>
    /// Adds a job, or brings an existing one back if its inputs have changed.
    /// </summary>
    /// <remarks>
    /// This is the whole of "work is enqueued by invalidation, never by a button". Asking for the
    /// same work twice does nothing; asking for it after the thread gained messages — which
    /// changes the input hash — puts it back in the queue and nothing else.
    /// </remarks>
    /// <returns>True when this left something to do.</returns>
    public bool Enqueue(
        AiJobKind kind, string subjectKind, string subjectId, string? inputHash, int priority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO ai_job (
                kind, subject_kind, subject_id, state, priority, attempts,
                input_hash, created_utc, updated_utc)
            VALUES ($kind, $subjectKind, $subjectId, 'pending', $priority, 0, $hash, $now, $now)
            ON CONFLICT (kind, subject_id, coalesce(prompt_version, ''), coalesce(model_version, ''))
            DO UPDATE SET
                state       = CASE
                                  WHEN ai_job.input_hash IS NOT excluded.input_hash THEN 'pending'
                                  ELSE ai_job.state
                              END,
                attempts    = CASE
                                  WHEN ai_job.input_hash IS NOT excluded.input_hash THEN 0
                                  ELSE ai_job.attempts
                              END,
                lease_utc   = NULL,
                priority    = excluded.priority,
                input_hash  = excluded.input_hash,
                updated_utc = excluded.updated_utc
            RETURNING state;
            """;

        command.Parameters.AddWithValue("$kind", Wire(kind));
        command.Parameters.AddWithValue("$subjectKind", subjectKind);
        command.Parameters.AddWithValue("$subjectId", subjectId);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$hash", (object?)inputHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());

        return command.ExecuteScalar() as string == "pending";
    }

    /// <summary>
    /// Takes the next job, if there is one.
    /// </summary>
    /// <remarks>
    /// Highest priority first, then oldest, so the queue drains in the order §11.1 describes —
    /// recent conversations and the people who are actually written to before a decade of
    /// everything else.
    /// </remarks>
    /// <param name="withheld">
    /// Kinds that may not be done now. Their jobs are skipped, not claimed: no attempt is used, no
    /// lease is taken, and they are exactly as they were when the reason goes away.
    /// </param>
    public AiJob? Claim(IReadOnlyCollection<AiJobKind>? withheld = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE ai_job
            SET state = 'running', attempts = attempts + 1, lease_utc = $lease, updated_utc = $now
            WHERE id = (
                SELECT id FROM ai_job
                WHERE state = 'pending'
                  AND kind NOT IN (SELECT value FROM json_each($withheld))
                ORDER BY priority DESC, id
                LIMIT 1
            )
            RETURNING id, kind, subject_kind, subject_id, state, priority, attempts, input_hash;
            """;

        command.Parameters.AddWithValue("$lease", Stamp(DateTime.UtcNow + Lease));
        command.Parameters.AddWithValue(
            "$withheld",
            System.Text.Json.JsonSerializer.Serialize((withheld ?? []).Select(Wire).ToArray()));
        command.Parameters.AddWithValue("$now", Now());

        using var reader = command.ExecuteReader();

        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Marks a job finished.</summary>
    public void Complete(long id, bool needsReview = false)
    {
        Update(id, needsReview ? "needs_review" : "done", lease: null, errorKind: null);
    }

    /// <summary>
    /// Records a failure, and decides whether it is worth trying again.
    /// </summary>
    /// <remarks>
    /// A job that has run out of attempts goes to <c>failed</c> rather than being deleted or
    /// retried forever. A queue that hides its failures reports full coverage of an archive it
    /// never finished reading.
    /// </remarks>
    public void Fail(long id, string errorKind, int attempts)
    {
        Update(id, attempts >= MaxAttempts ? "failed" : "pending", lease: null, errorKind: errorKind);
    }

    /// <summary>
    /// Puts jobs whose lease has expired back in the queue.
    /// </summary>
    /// <remarks>
    /// Called at startup and periodically. Without it, every job the app was in the middle of when
    /// it was closed stays <c>running</c> forever and the queue quietly stops making progress —
    /// which looks exactly like having finished.
    /// </remarks>
    /// <returns>How many were reclaimed.</returns>
    public int ReclaimExpired()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE ai_job
            SET state = 'pending', lease_utc = NULL, updated_utc = $now
            WHERE state = 'running' AND (lease_utc IS NULL OR lease_utc < $now);
            """;

        command.Parameters.AddWithValue("$now", Now());

        return command.ExecuteNonQuery();
    }

    /// <summary>How much work there is, by state — the numbers the progress line is built from.</summary>
    public AiJobCounts Counts(AiJobKind? kind = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT state, count(*)
            FROM ai_job
            WHERE $kind IS NULL OR kind = $kind
            GROUP BY state;
            """;

        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : Wire(kind.Value));

        long pending = 0, running = 0, done = 0, failed = 0, review = 0;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var count = reader.GetInt64(1);

            switch (reader.GetString(0))
            {
                case "pending": pending = count; break;
                case "running": running = count; break;
                case "done": done = count; break;
                case "failed": failed = count; break;
                default: review = count; break;
            }
        }

        return new AiJobCounts(pending, running, done, failed, review);
    }

    /// <summary>
    /// Puts every failed job back in the queue, with its attempts reset.
    /// </summary>
    /// <remarks>
    /// Failed is visible, not final. A job that ran out of attempts on something the endpoint got
    /// over — found against a local server that refused concurrent calls now and then — would
    /// otherwise stay failed for good: asking for the same work again finds the job it already
    /// has, at the same inputs, and leaves it where it is. This is the user's way of saying "try
    /// those again", and it is a button rather than a timer so a job failing for a real reason is
    /// not retried forever.
    /// </remarks>
    /// <returns>How many were put back.</returns>
    public int RetryFailed()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE ai_job
            SET state = 'pending', attempts = 0, lease_utc = NULL, last_error_kind = NULL, updated_utc = $now
            WHERE state = 'failed';
            """;

        command.Parameters.AddWithValue("$now", Now());

        return command.ExecuteNonQuery();
    }

    /// <summary>Empties the queue. Part of forgetting everything the AI layer produced.</summary>
    public void Clear()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM ai_job;";
        command.ExecuteNonQuery();
    }

    private void Update(long id, string state, string? lease, string? errorKind)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE ai_job
            SET state = $state, lease_utc = $lease, last_error_kind = $error, updated_utc = $now
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$lease", (object?)lease ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)errorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", id);

        command.ExecuteNonQuery();
    }

    private static AiJob Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Kind: ParseKind(reader.GetString(1)),
        SubjectKind: reader.GetString(2),
        SubjectId: reader.GetString(3),
        State: ParseState(reader.GetString(4)),
        Priority: reader.GetInt32(5),
        Attempts: reader.GetInt32(6),
        InputHash: reader.IsDBNull(7) ? null : reader.GetString(7));

    private static string Now() => Stamp(DateTime.UtcNow);

    private static string Stamp(DateTime moment) =>
        moment.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// The stored spelling of a kind.
    /// </summary>
    /// <remarks>
    /// Written out rather than taken from <c>ToString</c>: the CHECK constraint in the migration
    /// owns these strings, and renaming an enum member must not start writing a value the schema
    /// rejects.
    /// </remarks>
    private static string Wire(AiJobKind kind) => kind switch
    {
        AiJobKind.Segment => "segment",
        AiJobKind.Extract => "extract",
        AiJobKind.Adjudicate => "adjudicate",
        AiJobKind.Rollup => "rollup",
        AiJobKind.Diary => "diary",
        AiJobKind.Embed => "embed",
        AiJobKind.Transcribe => "transcribe",
        _ => "ocr",
    };

    private static AiJobKind ParseKind(string stored) => stored switch
    {
        "segment" => AiJobKind.Segment,
        "extract" => AiJobKind.Extract,
        "adjudicate" => AiJobKind.Adjudicate,
        "rollup" => AiJobKind.Rollup,
        "diary" => AiJobKind.Diary,
        "embed" => AiJobKind.Embed,
        "transcribe" => AiJobKind.Transcribe,
        _ => AiJobKind.Ocr,
    };

    private static AiJobState ParseState(string stored) => stored switch
    {
        "pending" => AiJobState.Pending,
        "running" => AiJobState.Running,
        "done" => AiJobState.Done,
        "failed" => AiJobState.Failed,
        _ => AiJobState.NeedsReview,
    };
}
