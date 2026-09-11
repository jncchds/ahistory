using System.Globalization;
using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Ai;

/// <summary>What a model call was for.</summary>
public enum AiPurpose
{
    Extract,
    Adjudicate,
    Rollup,
    Diary,
    Embed,
    ListModels,
    Test,
}

/// <summary>One call, as it is recorded.</summary>
/// <param name="FailureKind">
/// The kind, never the text: <c>timeout</c>, <c>unreachable</c>, <c>http_429</c>,
/// <c>bad_response</c>. A provider's error text can quote the request.
/// </param>
public sealed record AiInteraction
{
    public required AiPurpose Purpose { get; init; }

    public required LlmProviderKind Provider { get; init; }

    public required string Endpoint { get; init; }

    public required string Model { get; init; }

    public string? SubjectKind { get; init; }

    public string? SubjectId { get; init; }

    public int Attempt { get; init; } = 1;

    public required long DurationMs { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public int? TotalTokens { get; init; }

    public int ToolCallCount { get; init; }

    public string? FinishReason { get; init; }

    public int? HttpStatus { get; init; }

    public bool Failed { get; init; }

    public string? FailureKind { get; init; }

    /// <summary>Written only when the user has switched on prompt recording.</summary>
    public string? RequestJson { get; init; }

    public string? ResponseJson { get; init; }
}

/// <summary>Totals for one purpose, as the statistics page shows them.</summary>
public sealed record AiInteractionTotals(
    AiPurpose Purpose,
    long Calls,
    long Failures,
    long TotalTokens,
    long MedianDurationMs);

/// <summary>
/// The audit trail for every model call.
/// </summary>
/// <remarks>
/// <para>
/// Records counts, ids, durations and error kinds — the same rule the log follows (P6), for the
/// same reason: this table is in a file people copy around. The bodies of requests and responses
/// are written only when <see cref="AiSettings.RecordPromptBodies"/> says so, and that setting is
/// off by default because the request body is the user's correspondence.
/// </para>
/// <para>
/// Writes are single-row and outside any other transaction. A statistics row that blocked a
/// reader would be the smallest possible violation of P1 and still a violation.
/// </para>
/// </remarks>
public sealed class AiInteractions(Database database)
{
    /// <summary>How many rows are kept. Older ones are dropped as new ones arrive.</summary>
    private const int Keep = 5_000;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    public void Record(AiInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO ai_interaction (
                created_utc, purpose, provider, endpoint, model, subject_kind, subject_id,
                attempt, duration_ms, prompt_tokens, completion_tokens, total_tokens,
                tool_call_count, finish_reason, http_status, failed, failure_kind,
                request_json, response_json)
            VALUES (
                $created, $purpose, $provider, $endpoint, $model, $subjectKind, $subjectId,
                $attempt, $duration, $promptTokens, $completionTokens, $totalTokens,
                $toolCalls, $finishReason, $httpStatus, $failed, $failureKind,
                $request, $response);
            """;

        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$purpose", Wire(interaction.Purpose));
        command.Parameters.AddWithValue("$provider", interaction.Provider.ToString());
        command.Parameters.AddWithValue("$endpoint", interaction.Endpoint);
        command.Parameters.AddWithValue("$model", interaction.Model);
        command.Parameters.AddWithValue("$subjectKind", (object?)interaction.SubjectKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$subjectId", (object?)interaction.SubjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempt", interaction.Attempt);
        command.Parameters.AddWithValue("$duration", interaction.DurationMs);
        command.Parameters.AddWithValue("$promptTokens", (object?)interaction.PromptTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$completionTokens", (object?)interaction.CompletionTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$totalTokens", (object?)interaction.TotalTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolCalls", interaction.ToolCallCount);
        command.Parameters.AddWithValue("$finishReason", (object?)interaction.FinishReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$httpStatus", (object?)interaction.HttpStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$failed", interaction.Failed ? 1 : 0);
        command.Parameters.AddWithValue("$failureKind", (object?)interaction.FailureKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", (object?)interaction.RequestJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$response", (object?)interaction.ResponseJson ?? DBNull.Value);

        command.ExecuteNonQuery();

        Prune(connection);
    }

    /// <summary>Totals per purpose, most calls first.</summary>
    public IReadOnlyList<AiInteractionTotals> Totals()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        // The median rather than the mean, because one call that timed out at two minutes moves a
        // mean far enough to make a healthy run look broken.
        command.CommandText = """
            WITH ranked AS (
                SELECT purpose,
                       duration_ms,
                       row_number() OVER (PARTITION BY purpose ORDER BY duration_ms) AS rank,
                       count(*)     OVER (PARTITION BY purpose)                      AS n
                FROM ai_interaction
            ),
            medians AS (
                SELECT purpose, duration_ms AS median_ms FROM ranked WHERE rank = (n + 1) / 2
            )
            SELECT i.purpose,
                   count(*)                         AS calls,
                   sum(i.failed)                    AS failures,
                   coalesce(sum(i.total_tokens), 0) AS tokens,
                   coalesce(max(m.median_ms), 0)    AS median_ms
            FROM ai_interaction AS i
            LEFT JOIN medians AS m ON m.purpose = i.purpose
            GROUP BY i.purpose
            ORDER BY calls DESC;
            """;

        var totals = new List<AiInteractionTotals>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            totals.Add(new AiInteractionTotals(
                Purpose: Parse(reader.GetString(0)),
                Calls: reader.GetInt64(1),
                Failures: reader.GetInt64(2),
                TotalTokens: reader.GetInt64(3),
                MedianDurationMs: reader.GetInt64(4)));
        }

        return totals;
    }

    /// <summary>The most recent failure kinds and how often each happened.</summary>
    public IReadOnlyList<(string Kind, long Count)> FailureKinds(int limit = 8)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT coalesce(failure_kind, 'unknown') AS kind, count(*) AS n
            FROM ai_interaction
            WHERE failed = 1
            GROUP BY kind
            ORDER BY n DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        var kinds = new List<(string, long)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            kinds.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return kinds;
    }

    /// <summary>Deletes every recorded call. The statistics, and nothing else.</summary>
    public void Clear()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM ai_interaction;";
        command.ExecuteNonQuery();
    }

    private static void Prune(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            DELETE FROM ai_interaction
            WHERE id <= (SELECT max(id) - $keep FROM ai_interaction);
            """;

        command.Parameters.AddWithValue("$keep", Keep);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The stored spelling of a purpose.
    /// </summary>
    /// <remarks>
    /// Written out rather than taken from <c>ToString</c>: the CHECK constraint in the migration
    /// is the authority on these strings, and renaming an enum member must not silently start
    /// writing a value the schema rejects.
    /// </remarks>
    private static string Wire(AiPurpose purpose) => purpose switch
    {
        AiPurpose.Extract => "extract",
        AiPurpose.Adjudicate => "adjudicate",
        AiPurpose.Rollup => "rollup",
        AiPurpose.Diary => "diary",
        AiPurpose.Embed => "embed",
        AiPurpose.ListModels => "list_models",
        _ => "test",
    };

    private static AiPurpose Parse(string stored) => stored switch
    {
        "extract" => AiPurpose.Extract,
        "adjudicate" => AiPurpose.Adjudicate,
        "rollup" => AiPurpose.Rollup,
        "diary" => AiPurpose.Diary,
        "embed" => AiPurpose.Embed,
        "list_models" => AiPurpose.ListModels,
        _ => AiPurpose.Test,
    };
}
