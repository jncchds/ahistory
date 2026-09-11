using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Ai.Extraction;

/// <summary>What one extraction wrote.</summary>
public sealed record ExtractionResult(
    string ArtifactId, int Facts, int Corroborations, int Contradictions, int Retracted, bool NothingToRecord);

/// <summary>
/// Commits everything a run staged, in one short transaction.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is written while a model call is in flight — the whole conversation happens first and
/// lands here afterwards. That is P1's hard rule, and the reason reading the archive stays fast
/// during a drain that runs for hours: SQLite has one writer, and a transaction held open across a
/// thirty-second call stalls every other write in the app.
/// </para>
/// <para>
/// Facts are append-only. A correction is a new row and the old one is closed out; a re-run at a
/// new prompt version <b>retracts</b> in assertion time rather than deleting, because those facts
/// were believed and a diary narrating 2019 must be able to say what was believed then.
/// </para>
/// </remarks>
public sealed class FactWriter(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Writes a run's staged output against the session it came from.</summary>
    public ExtractionResult Write(
        ExtractionWindow window,
        ToolDispatcher staged,
        string model,
        string modelVersion,
        string promptVersion,
        string language,
        string inputHash)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(staged);

        var artifactId = "a_" + Hash($"{window.SessionId}|{promptVersion}|{model}|{inputHash}")[..24];
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var retracted = RetractPreviousRuns(connection, window.SessionId, artifactId, now);

        InsertArtifact(connection, artifactId, window, staged, model, modelVersion, promptVersion,
            language, inputHash, now);

        var written = 0;

        // Ids come from the position in the run, not from how many were written, so a fact the
        // user had already ruled on does not shift the id of every fact after it.
        for (var i = 0; i < staged.Facts.Count; i++)
        {
            if (InsertFact(connection, $"f_{Hash($"{artifactId}|{i}")[..24]}", staged.Facts[i], artifactId, now))
            {
                written++;
            }
        }

        foreach (var annotation in staged.Corroborations.Concat(staged.Contradictions))
        {
            foreach (var citation in annotation.Citations)
            {
                InsertCitation(connection, annotation.FactId, citation);
            }
        }

        transaction.Commit();

        return new ExtractionResult(
            ArtifactId: artifactId,
            Facts: written,
            Corroborations: staged.Corroborations.Count,
            Contradictions: staged.Contradictions.Count,
            Retracted: retracted,
            NothingToRecord: staged.NothingToRecord);
    }

    /// <summary>
    /// Stops believing what an earlier run of this session concluded.
    /// </summary>
    /// <remarks>
    /// Assertion time, not event time: the facts stop being believed, they do not stop having been
    /// believed. And never anything the user touched — a correction retracted by the next prompt
    /// improvement is what would make the facts panel not worth correcting (§5.6).
    /// </remarks>
    private static int RetractPreviousRuns(
        SqliteConnection connection, string sessionId, string artifactId, string now)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE fact
            SET retracted_utc = $now
            WHERE retracted_utc IS NULL
              AND source = 'extracted'
              AND derived_artifact_id IN (
                  SELECT id FROM derived_artifact
                  WHERE kind = 'session_extract'
                    AND source_session_id = $session
                    AND id <> $artifact
              );
            """;

        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$artifact", artifactId);

        return command.ExecuteNonQuery();
    }

    private static void InsertArtifact(
        SqliteConnection connection,
        string artifactId,
        ExtractionWindow window,
        ToolDispatcher staged,
        string model,
        string modelVersion,
        string promptVersion,
        string language,
        string inputHash,
        string now)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO derived_artifact (
                id, kind, source_session_id, engine, model, model_version, prompt_version,
                language, confidence, payload_json, input_hash, created_utc,
                window_start_unix, window_end_unix)
            VALUES ($id, 'session_extract', $session, 'llm', $model, $modelVersion, $promptVersion,
                    $language, NULL, $payload, $hash, $now, $start, $end)
            ON CONFLICT (id) DO UPDATE SET
                payload_json = excluded.payload_json,
                created_utc  = excluded.created_utc;
            """;

        var payload = JsonSerializer.Serialize(new
        {
            nothing_to_record = staged.NothingToRecord,
            reason = staged.NothingReason,
            facts = staged.Facts.Count,
            corroborations = staged.Corroborations,
            contradictions = staged.Contradictions,
        });

        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$session", window.SessionId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$modelVersion", modelVersion);
        command.Parameters.AddWithValue("$promptVersion", promptVersion);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", inputHash);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$start", window.Messages.Count == 0 ? 0 : window.Messages[0].SentAtUnix);
        command.Parameters.AddWithValue("$end", window.Messages.Count == 0 ? 0 : window.Messages[^1].SentAtUnix);

        command.ExecuteNonQuery();
    }

    /// <returns>False when the user had already ruled on this, and nothing new was written.</returns>
    private static bool InsertFact(
        SqliteConnection connection, string factId, StagedFact fact, string artifactId, string now)
    {
        var edgeId = fact.SubjectPair is { } pair ? EnsureEdge(connection, pair, now) : null;

        // The user has already ruled on this — deleted it, or written their own version of it. A
        // run that reads the same thing out of the same messages records that it did, against the
        // user's row, and does not bring back what they removed (ai-plan.md §5.6).
        if (fact.SupersedesFactId is null
            && UserVerdict(connection, fact.SubjectPersonId, edgeId, fact.Predicate, fact.ObjectText) is { } ruled)
        {
            foreach (var citation in fact.Citations)
            {
                InsertCitation(connection, ruled, citation with { Role = "corroborates" });
            }

            return false;
        }

        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO fact (
                id, subject_person_id, subject_edge_id, predicate, object_text, claim_text,
                evidence_kind, origin_kind, confidence, valid_from_utc, valid_to_utc,
                asserted_utc, derived_artifact_id, source)
            VALUES ($id, $person, $edge, $predicate, $object, $claim,
                    $evidence, $origin, $confidence, $from, $to, $now, $artifact, 'extracted');
            """;

        command.Parameters.AddWithValue("$id", factId);
        command.Parameters.AddWithValue("$person", (object?)fact.SubjectPersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$edge", (object?)edgeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$predicate", fact.Predicate);
        command.Parameters.AddWithValue("$object", fact.ObjectText);
        command.Parameters.AddWithValue("$claim", fact.ClaimText);
        command.Parameters.AddWithValue("$evidence", fact.EvidenceKind);
        command.Parameters.AddWithValue("$origin", fact.OriginKind);
        command.Parameters.AddWithValue("$confidence", fact.Confidence);
        command.Parameters.AddWithValue("$from", (object?)fact.ValidFromUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)fact.ValidToUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$artifact", artifactId);

        command.ExecuteNonQuery();

        foreach (var citation in fact.Citations)
        {
            InsertCitation(connection, factId, citation);
        }

        if (fact.SupersedesFactId is { } superseded)
        {
            Close(connection, superseded, factId, fact.ValidFromUtc);
        }

        return true;
    }

    /// <summary>
    /// The user's own row for this claim, if they have one.
    /// </summary>
    /// <remarks>
    /// Matched on the normalized key — subject, predicate, value — which is what the prompt asks
    /// the model to keep lowercase English precisely so it can be matched. SQLite's lower() only
    /// folds ASCII, and that is enough for a key kept in lowercase English; the readable claim,
    /// which may be in any language, is never compared.
    /// </remarks>
    private static string? UserVerdict(
        SqliteConnection connection, string? personId, string? edgeId, string predicate, string objectText)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id FROM fact
            WHERE predicate = $predicate
              AND lower(object_text) = lower($object)
              AND source IN ('user_deleted', 'user_edited')
              AND superseded_by IS NULL
              AND ((subject_person_id IS NOT NULL AND subject_person_id = $person)
                OR (subject_edge_id IS NOT NULL AND subject_edge_id = $edge))
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$predicate", predicate);
        command.Parameters.AddWithValue("$object", objectText);
        command.Parameters.AddWithValue("$person", (object?)personId ?? DBNull.Value);
        command.Parameters.AddWithValue("$edge", (object?)edgeId ?? DBNull.Value);

        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Closes out the fact this one replaces.
    /// </summary>
    /// <remarks>
    /// Event time, not assertion time: "works at Acme" from 2019 is not wrong and was not
    /// mistakenly believed — it expired. The date the new fact starts is the date the old one
    /// stops, so a diary rendering 2019 still finds the old value and the profile page finds the
    /// new one (§7).
    /// </remarks>
    private static void Close(
        SqliteConnection connection, string oldFactId, string newFactId, string? validFromUtc)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE fact
            SET superseded_by = $new,
                valid_to_utc  = coalesce(valid_to_utc, $validTo)
            WHERE id = $old;
            """;

        command.Parameters.AddWithValue("$new", newFactId);
        command.Parameters.AddWithValue("$validTo", (object?)validFromUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$old", oldFactId);

        command.ExecuteNonQuery();
    }

    private static void InsertCitation(SqliteConnection connection, string factId, StagedCitation citation)
    {
        using var command = connection.CreateCommand();

        // The same message can be cited twice for the same fact in the same role only once, which
        // is what the unique index says; a model repeating itself is not an error.
        command.CommandText = """
            INSERT INTO fact_citation (fact_id, message_id, role, quote)
            VALUES ($fact, $message, $role, $quote)
            ON CONFLICT (fact_id, message_id, role) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$fact", factId);
        command.Parameters.AddWithValue("$message", citation.MessageId);
        command.Parameters.AddWithValue("$role", citation.Role);
        command.Parameters.AddWithValue("$quote", (object?)citation.Quote ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>The edge for a pair, created if this is the first thing said about them.</summary>
    private static string EnsureEdge(SqliteConnection connection, (string A, string B) pair, string now)
    {
        var id = "e_" + Hash($"{pair.A}|{pair.B}")[..24];

        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO person_edge (id, person_a_id, person_b_id, created_utc)
            VALUES ($id, $a, $b, $now)
            ON CONFLICT (person_a_id, person_b_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$a", pair.A);
        command.Parameters.AddWithValue("$b", pair.B);
        command.Parameters.AddWithValue("$now", now);

        command.ExecuteNonQuery();

        // The insert may have done nothing because the edge was already there under another id.
        using var existing = connection.CreateCommand();

        existing.CommandText =
            "SELECT id FROM person_edge WHERE person_a_id = $a AND person_b_id = $b;";

        existing.Parameters.AddWithValue("$a", pair.A);
        existing.Parameters.AddWithValue("$b", pair.B);

        return (string)existing.ExecuteScalar()!;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
