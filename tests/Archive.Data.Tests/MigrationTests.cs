namespace Archive.Data.Tests;

/// <summary>
/// Checks the schema, the migration runner, and the pragmas the schema depends on.
/// </summary>
public sealed class MigrationTests
{
    private static readonly string[] ExpectedTables =
    [
        "derived_artifact", "fact", "fact_citation", "identity", "identity_person", "import",
        "media", "message", "message_import", "message_media", "message_revision", "person",
        "person_edge", "reaction", "save_meta", "schema_migration", "search_document",
        "session", "thread", "thread_participant",
    ];

    [Fact]
    public void Migration_creates_every_table()
    {
        using var db = new TempDatabase();

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        var found = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            found.Add(reader.GetString(0));
        }

        foreach (var table in ExpectedTables)
        {
            Assert.Contains(table, found);
        }
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        using var db = new TempDatabase();

        var before = db.Database.SchemaFingerprint();
        var appliedBefore = db.Scalar<long>("SELECT count(*) FROM schema_migration;");

        db.Database.Migrate();

        Assert.Equal(before, db.Database.SchemaFingerprint());
        Assert.Equal(appliedBefore, db.Scalar<long>("SELECT count(*) FROM schema_migration;"));
    }

    [Fact]
    public void Every_migration_file_is_recorded()
    {
        using var db = new TempDatabase();

        var expected = Database.EmbeddedMigrations().Select(m => m.Name).ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Length, db.Scalar<long>("SELECT count(*) FROM schema_migration;"));
    }

    [Fact]
    public void Journal_mode_is_wal()
    {
        using var db = new TempDatabase();

        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode;"));
    }

    [Fact]
    public void Foreign_keys_are_enforced()
    {
        using var db = new TempDatabase();

        Assert.Equal(1, db.Scalar<long>("PRAGMA foreign_keys;"));
    }

    [Fact]
    public void Recursive_triggers_are_on()
    {
        using var db = new TempDatabase();

        Assert.Equal(1, db.Scalar<long>("PRAGMA recursive_triggers;"));
    }

    /// <summary>
    /// §1: accidentally merging a contact into the owner poisons the entire knowledge base, so a
    /// second owner is made unrepresentable by the storage layer rather than guarded in the UI.
    /// </summary>
    [Fact]
    public void A_second_owner_cannot_be_inserted()
    {
        using var db = new TempDatabase();

        db.Execute("""
            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p1', 'Me', 1, '2020-01-01T00:00:00.0000000+00:00');
            """);

        var second = Assert.ThrowsAny<Exception>(() => db.Execute("""
            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p2', 'Also me', 1, '2020-01-01T00:00:00.0000000+00:00');
            """));

        Assert.Contains("UNIQUE", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Non-owner persons are unconstrained — the partial index must not catch them.</summary>
    [Fact]
    public void Many_non_owner_persons_are_allowed()
    {
        using var db = new TempDatabase();

        db.Execute("""
            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p1', 'Sam', 0, '2020-01-01T00:00:00.0000000+00:00'),
                   ('p2', 'Alex', 0, '2020-01-01T00:00:00.0000000+00:00'),
                   ('p3', 'Kim', 0, '2020-01-01T00:00:00.0000000+00:00');
            """);

        Assert.Equal(3, db.Scalar<long>("SELECT count(*) FROM person;"));
    }

    /// <summary>A fact describes a person or a pair, never both and never neither (§7).</summary>
    [Fact]
    public void A_fact_must_have_exactly_one_subject()
    {
        using var db = new TempDatabase();

        Assert.ThrowsAny<Exception>(() => db.Execute("""
            INSERT INTO fact (id, predicate, object_text, claim_text, evidence_kind, origin_kind,
                              confidence, asserted_utc, derived_artifact_id)
            VALUES ('f1', 'works_at', 'Acme', 'Sam works at Acme', 'self_report', 'dm',
                    0.9, '2020-01-01T00:00:00.0000000+00:00', 'da1');
            """));
    }
}
