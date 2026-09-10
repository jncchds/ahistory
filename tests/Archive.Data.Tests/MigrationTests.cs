using System.Security.Cryptography;
using System.Text;

namespace Archive.Data.Tests;

/// <summary>
/// Checks the schema, the migration runner, and the pragmas the schema depends on.
/// </summary>
public sealed class MigrationTests
{
    /// <summary>
    /// Every migration that has ever been applied to a save, and the content it had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Migrations are recorded in a save by filename, so a save carries no memory of what the file
    /// said when it ran. Editing an applied migration therefore changes what new saves get and
    /// leaves every existing one behind, with nothing anywhere to notice the difference.
    /// </para>
    /// <para>
    /// That is not hypothetical — a column was dropped from 001_core.sql after saves existed, and
    /// the first sign of it was a NOT NULL failure on a column the code no longer mentioned,
    /// raised from the middle of an import (decisions.md D23). This table is what makes that a
    /// failing test on the commit that causes it rather than a stack trace on someone's machine.
    /// </para>
    /// <para>
    /// <b>Adding a migration adds a line here. Changing one of these files is the mistake this
    /// exists to catch</b> — the fix is a new numbered migration, never an edit to an old one.
    /// Updating a hash to make this pass is the same mistake with an extra step.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> ReleasedMigrations = new(StringComparer.Ordinal)
    {
        ["001_core.sql"] = "02e3ea0aa020b6f2035a87ecb767501bde885e376a9cb0a83f37d424e4a260a5",
        ["002_derived.sql"] = "04a2b60b45f2f8c3c28334ec36be89e28a29114952607fcf19e6aca67fa885fb",
        ["003_search.sql"] = "753e836ac627f6f75c98f153da0512d2b81e65e762cb20357a7b5b3b2b0e4f93",
        ["004_provenance.sql"] = "fbc6a26e1d1c45908239f2592e5451b80431bd86f2c7d70894acdf62cbfe6374",
        ["005_merge_suggestions.sql"] = "ae7b8fd74444b141f6f57fc3728fd7a01b2371696922d5e4132e18a9257f00a6",
    };

    /// <summary>
    /// Migrations are append-only: a file that has shipped keeps the content it shipped with.
    /// </summary>
    [Fact]
    public void No_migration_that_has_shipped_has_been_edited()
    {
        foreach (var (name, sql) in Database.EmbeddedMigrations())
        {
            if (!ReleasedMigrations.TryGetValue(name, out var expected))
            {
                // A genuinely new migration. Add it to the table above, with this hash.
                continue;
            }

            Assert.Equal(expected, Sha256(sql));
        }
    }

    /// <summary>
    /// A migration cannot be quietly withdrawn either: removing one has the same effect as
    /// editing it, since existing saves have already run it.
    /// </summary>
    [Fact]
    public void No_migration_that_has_shipped_has_been_removed()
    {
        var present = Database.EmbeddedMigrations().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in ReleasedMigrations.Keys)
        {
            Assert.Contains(name, present);
        }
    }

    /// <summary>
    /// Hashes the migration with line endings normalized.
    /// </summary>
    /// <remarks>
    /// .gitattributes pins these files to LF, so this should never matter. It costs one line and
    /// means a checkout that has somehow acquired CRLF fails on something more useful than a
    /// hash mismatch on every migration at once.
    /// </remarks>
    private static string Sha256(string sql) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

    private static readonly string[] ExpectedTables =
    [
        "derived_artifact", "fact", "fact_citation", "identity", "identity_person", "import",
        "import_source", "media", "message", "message_media", "message_revision", "message_source",
        "person", "person_edge", "reaction", "save_meta", "save_provenance", "schema_migration",
        "search_document",
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

    /// <summary>
    /// A save whose schema is not the one the migrations produce is refused when it is opened.
    /// </summary>
    /// <remarks>
    /// Migrations are recorded by name, so a migration edited after it has been applied leaves
    /// existing saves on the old schema with nothing to notice. That happened: a column was
    /// dropped from 001_core.sql once saves already existed, and it surfaced as a NOT NULL
    /// failure on a column the code no longer mentioned, raised from the middle of an import.
    ///
    /// An added column stands in for that here. Any drift moves the fingerprint, so the check
    /// does not care which direction it went or what caused it.
    /// </remarks>
    [Fact]
    public void A_save_whose_schema_drifted_is_refused_when_it_is_opened()
    {
        using var db = new TempDatabase();

        db.Execute("ALTER TABLE message_source ADD COLUMN seen_utc TEXT;");

        Assert.Equal(SchemaState.Diverged, db.Database.Inspect().State);

        var error = Assert.Throws<InvalidOperationException>(() => db.Database.Migrate());

        // The message has to be enough to act on without reading the source.
        Assert.Contains("new save", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Puts a save back to where it stood before <c>003_search.sql</c> ran.
    /// </summary>
    /// <remarks>
    /// Dropping what a migration created is the only honest way to make a save that is genuinely
    /// behind: deleting the schema_migration row alone would leave the objects in place, which is
    /// a save that has drifted, not one that is behind — a different case with a different answer.
    /// </remarks>
    private static void Rewind(TempDatabase db)
    {
        db.Execute("""
            DROP TRIGGER trg_message_ai;
            DROP TRIGGER trg_message_au;
            DROP TRIGGER trg_search_document_ai;
            DROP TRIGGER trg_search_document_ad;
            DROP TRIGGER trg_search_document_au;
            DROP TABLE search_fts;
            DROP TABLE search_document;
            DROP TABLE save_provenance;
            DROP TABLE merge_dismissal;
            DELETE FROM schema_migration
            WHERE name IN ('003_search.sql', '004_provenance.sql', '005_merge_suggestions.sql');
            """);
    }

    /// <summary>
    /// A save from an older version is recognized as behind, and named as such rather than
    /// treated as broken.
    /// </summary>
    [Fact]
    public void A_save_from_an_older_version_is_behind_and_says_what_would_run()
    {
        using var db = new TempDatabase();
        Rewind(db);

        var status = db.Database.Inspect();

        Assert.Equal(SchemaState.Behind, status.State);
        Assert.True(status.CanUpgrade);
        Assert.Equal(["003_search.sql", "004_provenance.sql", "005_merge_suggestions.sql"], status.Pending);
    }

    /// <summary>
    /// Opening a save that is behind does not migrate it.
    /// </summary>
    /// <remarks>
    /// The upgrade is one-way, so it never happens as a side effect of opening a save. The
    /// exception carries what is needed to ask the question: the path and what would run.
    /// </remarks>
    [Fact]
    public void A_save_that_is_behind_is_not_upgraded_just_by_opening_it()
    {
        using var db = new TempDatabase();
        Rewind(db);

        var error = Assert.Throws<SchemaUpgradeRequiredException>(() => db.Database.Migrate());

        Assert.Equal(["003_search.sql", "004_provenance.sql", "005_merge_suggestions.sql"], error.Pending);
        Assert.Equal(db.Database.DatabasePath, error.SavePath);

        // Still behind: asking must not be the same as doing.
        Assert.Equal(SchemaState.Behind, db.Database.Inspect().State);
    }

    [Fact]
    public void Upgrading_carries_the_save_forward_and_leaves_a_copy_of_what_it_was()
    {
        using var db = new TempDatabase();
        Rewind(db);

        var backup = db.Database.DatabasePath + ".pre-003";
        var applied = db.Database.Upgrade(backup);

        Assert.Equal(["003_search.sql", "004_provenance.sql", "005_merge_suggestions.sql"], applied);
        Assert.Equal(SchemaState.UpToDate, db.Database.Inspect().State);

        // The copy is a database in its own right, still standing where the save did.
        Assert.True(File.Exists(backup));

        using var copy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backup};Mode=ReadOnly");
        copy.Open();

        using var command = copy.CreateCommand();
        command.CommandText = "SELECT count(*) FROM schema_migration;";

        Assert.Equal(2L, command.ExecuteScalar());
    }

    /// <summary>
    /// A backup never overwrites: it may be the only copy of a save nobody can rebuild.
    /// </summary>
    [Fact]
    public void A_backup_refuses_to_overwrite_an_existing_file()
    {
        using var db = new TempDatabase();
        Rewind(db);

        var backup = db.Database.DatabasePath + ".pre-003";
        File.WriteAllText(backup, "an earlier copy");

        Assert.Throws<InvalidOperationException>(() => db.Database.Upgrade(backup));

        // And the save is untouched, because the copy is taken before anything runs.
        Assert.Equal(SchemaState.Behind, db.Database.Inspect().State);
        Assert.Equal("an earlier copy", File.ReadAllText(backup));
    }

    /// <summary>
    /// A save from a newer build is not a save to start over from.
    /// </summary>
    /// <remarks>
    /// This is the case that most needs telling apart from the others: the advice for a diverged
    /// save is to import into a new one, and following that here would discard the newer of the
    /// two saves. Migrations only move forward, so the app is what is out of date.
    /// </remarks>
    [Fact]
    public void A_save_from_a_newer_version_says_to_update_the_app()
    {
        using var db = new TempDatabase();

        db.Execute("""
            CREATE TABLE annotation (id INTEGER PRIMARY KEY) STRICT;
            INSERT INTO schema_migration (name, applied_utc)
            VALUES ('004_annotations.sql', '2026-10-01T00:00:00Z');
            """);

        var status = db.Database.Inspect();

        Assert.Equal(SchemaState.Ahead, status.State);
        Assert.Equal(["004_annotations.sql"], status.Unknown);
        Assert.False(status.CanUpgrade);

        var error = Assert.Throws<InvalidOperationException>(() => db.Database.Migrate());

        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
        Assert.Contains("Update ahistory", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("import into a new save", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A new save records the build that made it.
    /// </summary>
    [Fact]
    public void A_new_save_records_which_build_created_it()
    {
        using var db = new TempDatabase();

        Assert.Equal(Database.AppVersion, db.Scalar<string>("SELECT created_by FROM save_provenance;"));
        Assert.Equal(Database.AppVersion, db.Scalar<string>("SELECT upgraded_by FROM save_provenance;"));
    }

    /// <summary>
    /// An upgraded save records who upgraded it, and does not claim to have been created by them.
    /// </summary>
    /// <remarks>
    /// created_by stays null on a save that predates the table. It was made before anything
    /// recorded this, and filling it in with whichever build happened to run the upgrade would be
    /// inventing the very history the field exists to report.
    /// </remarks>
    [Fact]
    public void An_upgraded_save_records_the_upgrade_and_not_a_creation()
    {
        using var db = new TempDatabase();
        Rewind(db);

        db.Database.Upgrade(backupPath: null);

        Assert.Null(db.Scalar<string>("SELECT created_by FROM save_provenance;"));
        Assert.Equal(Database.AppVersion, db.Scalar<string>("SELECT upgraded_by FROM save_provenance;"));
    }

    [Fact]
    public void A_save_with_every_migration_applied_is_up_to_date()
    {
        using var db = new TempDatabase();

        var status = db.Database.Inspect();

        Assert.Equal(SchemaState.UpToDate, status.State);
        Assert.True(status.IsUsable);
        Assert.Empty(status.Pending);
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
