using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Archive.Data;

namespace Archive.Ai.Extraction;

/// <summary>A fact as the panel shows it.</summary>
/// <param name="FirstMessageId">
/// The message that asserted it — what clicking the fact opens. Every fact renders with its
/// evidence one click away, because the citation rule catches the lazy failure and only a person
/// reading the source catches the confident one (§5.4).
/// </param>
public sealed record FactView(
    string Id,
    string? SubjectPersonId,
    string? OtherPersonName,
    string Predicate,
    string ObjectText,
    string ClaimText,
    string EvidenceKind,
    string OriginKind,
    double Confidence,
    string? ValidFromUtc,
    string? ValidToUtc,
    string Source,
    long? FirstMessageId,
    int CitationCount);

/// <summary>
/// What the facts panel reads and what a person's corrections write.
/// </summary>
/// <remarks>
/// <para>
/// A correction is a fact the model did not produce, and it must outrank everything the model
/// produces afterwards. An edit is therefore a new row with <c>source = 'user_edited'</c> that
/// supersedes the old one — append-only, like every other change to a fact — and a deletion is a
/// tombstone rather than a missing row, so that a later run asserting the same thing can see it was
/// rejected and stay rejected (ai-plan.md §5.6).
/// </para>
/// <para>
/// Both are one short statement or one short transaction. The panel is used while a drain may be
/// running, and SQLite has one writer.
/// </para>
/// </remarks>
public sealed class FactStore(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>
    /// What is currently believed about a person, their relationships included.
    /// </summary>
    /// <remarks>
    /// Live means not superseded, not retracted, and not deleted by the user. A fact on an edge is
    /// shown to both people in it — §7's point is that "met in Berlin in 2015" is one row rendered
    /// in two diaries, not two rows telling two stories.
    /// </remarks>
    public IReadOnlyList<FactView> ForPerson(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT f.id, f.subject_person_id,
                   (SELECT p.display_name FROM person AS p
                    WHERE p.id = CASE WHEN e.person_a_id = $person THEN e.person_b_id ELSE e.person_a_id END),
                   f.predicate, f.object_text, f.claim_text, f.evidence_kind, f.origin_kind,
                   f.confidence, f.valid_from_utc, f.valid_to_utc, f.source,
                   (SELECT min(c.message_id) FROM fact_citation AS c
                    WHERE c.fact_id = f.id AND c.role = 'asserts'),
                   -- Everything folded into this fact counts as evidence for it: that is what a
                   -- merge is for (A4), and the duplicates keep their citations on their own rows.
                   (SELECT count(*) FROM fact_citation AS c
                    WHERE c.fact_id = f.id
                       OR c.fact_id IN (SELECT d.id FROM fact AS d WHERE d.merged_into = f.id))
            FROM fact AS f
            LEFT JOIN person_edge AS e ON e.id = f.subject_edge_id
            WHERE (f.subject_person_id = $person OR e.person_a_id = $person OR e.person_b_id = $person)
              AND f.superseded_by IS NULL
              AND f.merged_into IS NULL
              AND f.retracted_utc IS NULL
              AND f.source <> 'user_deleted'
            ORDER BY f.source = 'extracted', f.confidence DESC, f.predicate;
            """;

        command.Parameters.AddWithValue("$person", personId);

        var facts = new List<FactView>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            facts.Add(new FactView(
                Id: reader.GetString(0),
                SubjectPersonId: reader.IsDBNull(1) ? null : reader.GetString(1),
                OtherPersonName: reader.IsDBNull(2) ? null : reader.GetString(2),
                Predicate: reader.GetString(3),
                ObjectText: reader.GetString(4),
                ClaimText: reader.GetString(5),
                EvidenceKind: reader.GetString(6),
                OriginKind: reader.GetString(7),
                Confidence: reader.GetDouble(8),
                ValidFromUtc: reader.IsDBNull(9) ? null : reader.GetString(9),
                ValidToUtc: reader.IsDBNull(10) ? null : reader.GetString(10),
                Source: reader.GetString(11),
                FirstMessageId: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                CitationCount: reader.GetInt32(13)));
        }

        return facts;
    }

    /// <summary>
    /// Replaces a fact with the user's version of it.
    /// </summary>
    /// <remarks>
    /// A new row, not an update: the model's version was believed until now, and a diary that
    /// narrates the past must still be able to say so. The correction carries the same citations,
    /// because it is a correction of what those messages say — not a claim with nothing behind it.
    /// </remarks>
    /// <returns>The id of the corrected fact.</returns>
    public string Edit(string factId, string objectText, string claimText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectText);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimText);

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var newId = "f_" + Hash($"{factId}|user|{now}")[..24];

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            // Confidence 1: the user is not guessing about their own correspondence. Everything
            // else — subject, predicate, validity, which run it hangs off — carries over.
            insert.CommandText = """
                INSERT INTO fact (
                    id, subject_person_id, subject_edge_id, predicate, object_text, claim_text,
                    evidence_kind, origin_kind, confidence, valid_from_utc, valid_to_utc,
                    asserted_utc, derived_artifact_id, source)
                SELECT $new, subject_person_id, subject_edge_id, predicate, $object, $claim,
                       evidence_kind, origin_kind, 1.0, valid_from_utc, valid_to_utc,
                       $now, derived_artifact_id, 'user_edited'
                FROM fact
                WHERE id = $old AND superseded_by IS NULL AND source <> 'user_deleted';
                """;

            insert.Parameters.AddWithValue("$new", newId);
            insert.Parameters.AddWithValue("$object", objectText.Trim());
            insert.Parameters.AddWithValue("$claim", claimText.Trim());
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$old", factId);

            if (insert.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("That fact has already been changed or removed.");
            }
        }

        using (var citations = connection.CreateCommand())
        {
            citations.CommandText = """
                INSERT INTO fact_citation (fact_id, message_id, role, quote)
                SELECT $new, message_id, role, quote FROM fact_citation WHERE fact_id = $old;
                """;

            citations.Parameters.AddWithValue("$new", newId);
            citations.Parameters.AddWithValue("$old", factId);
            citations.ExecuteNonQuery();
        }

        using (var close = connection.CreateCommand())
        {
            // What was folded into the old wording is evidence for the corrected one.
            close.CommandText = """
                UPDATE fact SET superseded_by = $new WHERE id = $old;
                UPDATE fact SET merged_into = $new WHERE merged_into = $old;
                """;
            close.Parameters.AddWithValue("$new", newId);
            close.Parameters.AddWithValue("$old", factId);
            close.ExecuteNonQuery();
        }

        transaction.Commit();

        return newId;
    }

    /// <summary>
    /// Removes a fact from what is believed, and remembers that the user removed it.
    /// </summary>
    /// <remarks>
    /// A tombstone, not a delete. The row keeps its subject, predicate and value so that the next
    /// run which reads the same thing out of the same messages recognises it as rejected and
    /// records a citation against the tombstone instead of bringing the fact back.
    ///
    /// Everything folded into it goes with it. They were merged because they say the same thing,
    /// and a removed fact whose restatements all reappeared would not have been removed.
    /// </remarks>
    public void Delete(string factId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE fact
            SET source = 'user_deleted', retracted_utc = coalesce(retracted_utc, $now)
            WHERE id = $id OR merged_into = $id;
            """;

        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", factId);
        command.ExecuteNonQuery();
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
