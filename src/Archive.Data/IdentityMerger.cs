namespace Archive.Data;

/// <summary>
/// Merging and unmerging contacts.
/// </summary>
/// <remarks>
/// §1: merging is repointing identities at a person. Message rows are never rewritten, which is
/// what makes unmerge trivial — and unmerge is needed, because matching people across platforms
/// on names and phone numbers gets it wrong.
/// </remarks>
public sealed class IdentityMerger(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>
    /// Points an identity at a different person.
    /// </summary>
    /// <param name="confirmOwnerMerge">
    /// Required when the target is the owner. §1: accidentally merging a contact into the owner
    /// poisons the entire knowledge base — every fact that contact ever stated becomes a fact
    /// about you — so it cannot happen by mis-clicking a list.
    /// </param>
    public void MergeInto(string identityId, string personId, bool confirmOwnerMerge = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT is_owner FROM person WHERE id = $person;";
            check.Parameters.AddWithValue("$person", personId);

            var isOwner = check.ExecuteScalar();

            if (isOwner is null or DBNull)
            {
                throw new InvalidOperationException($"No such person: '{personId}'.");
            }

            if (Convert.ToInt64(isOwner) == 1 && !confirmOwnerMerge)
            {
                throw new InvalidOperationException(
                    "Merging an identity into the owner must be confirmed explicitly: it makes "
                    + "everything that identity ever said a statement about you.");
            }
        }

        var previousPersonId = PersonOf(connection, transaction, identityId);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE identity_person
                SET person_id = $person, confidence = 'manual', linked_utc = $now
                WHERE identity_id = $identity;
                """;
            update.Parameters.AddWithValue("$person", personId);
            update.Parameters.AddWithValue("$identity", identityId);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

            if (update.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException($"No such identity: '{identityId}'.");
            }
        }

        // Moving someone's last account is merging the whole person, and what was learned about
        // them has to go where they went. Moving one of several leaves them in place, facts and all.
        if (previousPersonId is not null
            && !string.Equals(previousPersonId, personId, StringComparison.Ordinal)
            && !HasIdentities(connection, transaction, previousPersonId))
        {
            MoveKnowledge(connection, transaction, previousPersonId, personId);
        }

        RemoveEmptyPeople(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Moves every account of one person onto another.
    /// </summary>
    /// <remarks>
    /// Merging identities one at a time is the primitive; this is what someone actually wants when
    /// they have realized that two rows in the People list are one human. Doing it as three
    /// separate merges leaves the archive in a half-merged state between each, which is visible
    /// on the page and wrong if the third one fails.
    /// </remarks>
    /// <param name="confirmOwnerMerge">
    /// Required when the target is the owner, for the same reason as <see cref="MergeInto"/>, and
    /// with more at stake: this moves everything that person ever said at once.
    /// </param>
    public void MergePeople(string sourcePersonId, string targetPersonId, bool confirmOwnerMerge = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePersonId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPersonId);

        if (string.Equals(sourcePersonId, targetPersonId, StringComparison.Ordinal))
        {
            return;
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var sourceIsOwner = IsOwner(connection, transaction, sourcePersonId, nameof(sourcePersonId));
        var targetIsOwner = IsOwner(connection, transaction, targetPersonId, nameof(targetPersonId));

        if (targetIsOwner && !confirmOwnerMerge)
        {
            throw new InvalidOperationException(
                "Merging a person into the owner must be confirmed explicitly: it makes everything "
                + "they ever said a statement about you.");
        }

        // The owner is the archive's subject, and a merge that dissolves them leaves it with none.
        // Merging the other way round — the contact into you — is the operation that was meant,
        // and it is the one that already asks first.
        if (sourceIsOwner)
        {
            throw new InvalidOperationException(
                "The archive's owner cannot be merged into someone else. Merge the other account "
                + "into the owner instead.");
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE identity_person
                SET person_id = $target, confidence = 'manual', linked_utc = $now
                WHERE person_id = $source;
                """;
            update.Parameters.AddWithValue("$target", targetPersonId);
            update.Parameters.AddWithValue("$source", sourcePersonId);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.ExecuteNonQuery();
        }

        MoveKnowledge(connection, transaction, sourcePersonId, targetPersonId);
        RemoveEmptyPeople(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Carries what was learned about one person over to the person they turned out to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this a merge deleted it. The emptied person is removed, <c>fact</c> cascades from
    /// <c>person</c>, and the facts went with them — while the session they were read from kept its
    /// extract, so it counted as read and was never read again. Everything the model had said
    /// about someone vanished the moment their accounts were put together, and nothing would bring
    /// it back (011_refill_lost_facts.sql re-reads what was lost that way before this existed).
    /// </para>
    /// <para>
    /// Repointing the subject is not the rewrite §7 forbids: the claim, its citations and both time
    /// axes are untouched, exactly as a merge leaves a message's sender alone and moves the identity
    /// instead. Both sets of facts now sit on one person, where the merge step finds the ones that
    /// say the same thing.
    /// </para>
    /// <para>
    /// A fact about a pair moves to the same pair with the target in it, joining that edge if there
    /// already is one. A fact about the source and the target <i>together</i> is left to go: it
    /// describes a relationship between two accounts of one human, which the merge has just said
    /// does not exist.
    /// </para>
    /// <para>
    /// Diary entries and rollups are left to go too. They are prose written from the facts, the
    /// target's are out of date the moment these arrive, and the diary planner writes them again
    /// from the merged set — two of them for one person and month would be worse than none.
    /// </para>
    /// </remarks>
    private static void MoveKnowledge(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string sourcePersonId,
        string targetPersonId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE fact SET subject_person_id = $target WHERE subject_person_id = $source;

            -- An edge from the source to someone the target already has an edge with: its facts join
            -- that edge, and the source's copy goes with the source.
            UPDATE fact
            SET subject_edge_id = keep.id
            FROM person_edge AS moving
            JOIN person_edge AS keep
              ON keep.person_a_id = min($target, iif(moving.person_a_id = $source, moving.person_b_id, moving.person_a_id))
             AND keep.person_b_id = max($target, iif(moving.person_a_id = $source, moving.person_b_id, moving.person_a_id))
            WHERE fact.subject_edge_id = moving.id
              AND $source IN (moving.person_a_id, moving.person_b_id)
              AND $target NOT IN (moving.person_a_id, moving.person_b_id);

            -- Any other edge simply becomes the target's, kept in canonical order.
            UPDATE person_edge
            SET person_a_id = min($target, iif(person_a_id = $source, person_b_id, person_a_id)),
                person_b_id = max($target, iif(person_a_id = $source, person_b_id, person_a_id))
            WHERE $source IN (person_a_id, person_b_id)
              AND $target NOT IN (person_a_id, person_b_id)
              AND NOT EXISTS (
                  SELECT 1 FROM person_edge AS keep
                  WHERE keep.person_a_id = min($target, iif(person_edge.person_a_id = $source, person_edge.person_b_id, person_edge.person_a_id))
                    AND keep.person_b_id = max($target, iif(person_edge.person_a_id = $source, person_edge.person_b_id, person_edge.person_a_id)));

            -- Queued work about someone who is about to stop existing. The planner queues it again
            -- for the target, whose facts have just changed.
            DELETE FROM ai_job WHERE subject_kind = 'person' AND subject_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourcePersonId);
        command.Parameters.AddWithValue("$target", targetPersonId);
        command.ExecuteNonQuery();
    }

    /// <summary>The person an identity belongs to now, or null for an identity that is not one.</summary>
    private static string? PersonOf(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string identityId)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT person_id FROM identity_person WHERE identity_id = $identity;";
        read.Parameters.AddWithValue("$identity", identityId);

        return read.ExecuteScalar() as string;
    }

    private static bool HasIdentities(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string personId)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT EXISTS (SELECT 1 FROM identity_person WHERE person_id = $person);";
        read.Parameters.AddWithValue("$person", personId);

        return read.ExecuteScalar() is long found && found == 1;
    }

    /// <summary>Reads a person's owner flag, refusing an id that is not one.</summary>
    private static bool IsOwner(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string personId,
        string parameterName)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT is_owner FROM person WHERE id = $person;";
        check.Parameters.AddWithValue("$person", personId);

        var value = check.ExecuteScalar();

        return value is null or DBNull
            ? throw new InvalidOperationException($"No such person: '{personId}' ({parameterName}).")
            : Convert.ToInt64(value) == 1;
    }

    /// <summary>
    /// Detaches an identity onto a person of its own.
    /// </summary>
    /// <remarks>
    /// Refuses to detach a seed identity. That link came from the export's own
    /// personal_information rather than from a guess, and unmerging it would leave the archive
    /// without a definite "me" — which is what the whole knowledge base is oriented around (P5).
    /// </remarks>
    public string Unmerge(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        string displayName;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT i.display_name, ip.confidence
                FROM identity i JOIN identity_person ip ON ip.identity_id = i.id
                WHERE i.id = $identity;
                """;
            read.Parameters.AddWithValue("$identity", identityId);

            using var reader = read.ExecuteReader();

            if (!reader.Read())
            {
                throw new InvalidOperationException($"No such identity: '{identityId}'.");
            }

            displayName = reader.GetString(0);

            if (reader.GetString(1) == "seed")
            {
                throw new InvalidOperationException(
                    "This identity is the archive owner, taken from the export itself. "
                    + "Detaching it would leave the archive with no definite owner.");
            }
        }

        var personId = $"p:{identityId}:{Guid.NewGuid().ToString("N")[..8]}";

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                INSERT INTO person (id, display_name, is_owner, created_utc)
                VALUES ($id, $name, 0, $now);
                """;
            create.Parameters.AddWithValue("$id", personId);
            create.Parameters.AddWithValue("$name", displayName);
            create.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            create.ExecuteNonQuery();
        }

        using (var relink = connection.CreateCommand())
        {
            relink.Transaction = transaction;
            relink.CommandText = """
                UPDATE identity_person
                SET person_id = $person, confidence = 'manual', linked_utc = $now
                WHERE identity_id = $identity;
                """;
            relink.Parameters.AddWithValue("$person", personId);
            relink.Parameters.AddWithValue("$identity", identityId);
            relink.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            relink.ExecuteNonQuery();
        }

        RemoveEmptyPeople(connection, transaction);
        transaction.Commit();

        return personId;
    }

    /// <summary>Renames a person. Their identities keep the names the platforms gave them.</summary>
    public void Rename(string personId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE person SET display_name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$id", personId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops people who no longer have any identities.
    /// </summary>
    /// <remarks>
    /// A merge leaves the abandoned person behind with nothing pointing at it. The owner is
    /// exempt: an owner with no identities yet is a legitimate state, and deleting them would
    /// take the archive's subject with them.
    /// </remarks>
    private static void RemoveEmptyPeople(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM person
            WHERE is_owner = 0
              AND NOT EXISTS (SELECT 1 FROM identity_person ip WHERE ip.person_id = person.id);
            """;
        command.ExecuteNonQuery();
    }
}
