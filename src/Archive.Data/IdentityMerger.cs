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

        RemoveEmptyPeople(connection, transaction);
        transaction.Commit();
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
