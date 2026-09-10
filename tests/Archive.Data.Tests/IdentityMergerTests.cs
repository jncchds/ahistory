namespace Archive.Data.Tests;

/// <summary>
/// §1: merging contacts is repointing identities at a person, never rewriting messages. That is
/// what makes unmerge cheap — and unmerge is needed, because matching people across platforms on
/// names and phone numbers gets it wrong.
/// </summary>
public sealed class IdentityMergerTests
{
    private static (TempDatabase Db, IdentityMerger Merger) Fixture()
    {
        var db = new TempDatabase();
        Seed.Basics(db);
        Seed.People(db);
        Seed.Message(db, "tg/100/1", "the harbour was freezing");

        return (db, new IdentityMerger(db.Database));
    }

    [Fact]
    public void Merging_repoints_the_identity_without_touching_messages()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        var before = db.Scalar<string>("SELECT sender_identity_id FROM message WHERE uid = 'tg/100/1';");

        merger.MergeInto(Seed.IdentityId, Seed.OwnerPersonId, confirmOwnerMerge: true);

        Assert.Equal(
            Seed.OwnerPersonId,
            db.Scalar<string>($"SELECT person_id FROM identity_person WHERE identity_id = '{Seed.IdentityId}';"));

        // The message still points at the same identity. Nothing about it was rewritten.
        Assert.Equal(before, db.Scalar<string>("SELECT sender_identity_id FROM message WHERE uid = 'tg/100/1';"));
    }

    /// <summary>
    /// §1: accidentally merging a contact into the owner poisons the entire knowledge base —
    /// everything that contact ever said becomes a statement about you. It must not be reachable
    /// by mis-clicking a list.
    /// </summary>
    [Fact]
    public void Merging_into_the_owner_requires_confirmation()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        var refused = Assert.Throws<InvalidOperationException>(
            () => merger.MergeInto(Seed.IdentityId, Seed.OwnerPersonId));

        Assert.Contains("confirmed explicitly", refused.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing moved.
        Assert.Equal(
            Seed.SamPersonId,
            db.Scalar<string>($"SELECT person_id FROM identity_person WHERE identity_id = '{Seed.IdentityId}';"));
    }

    [Fact]
    public void Merging_into_an_ordinary_person_needs_no_confirmation()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        db.Execute("""
            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p:alex', 'Alex', 0, '2020-01-01T00:00:00.0000000+00:00');
            """);

        merger.MergeInto(Seed.IdentityId, "p:alex");

        Assert.Equal(
            "p:alex",
            db.Scalar<string>($"SELECT person_id FROM identity_person WHERE identity_id = '{Seed.IdentityId}';"));
    }

    /// <summary>A merge leaves the abandoned person with nothing pointing at it.</summary>
    [Fact]
    public void Merging_removes_the_person_left_behind()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        merger.MergeInto(Seed.IdentityId, Seed.OwnerPersonId, confirmOwnerMerge: true);

        Assert.Equal(0, db.Scalar<long>($"SELECT count(*) FROM person WHERE id = '{Seed.SamPersonId}';"));
    }

    [Fact]
    public void Unmerging_gives_the_identity_a_person_of_its_own()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        merger.MergeInto(Seed.IdentityId, Seed.OwnerPersonId, confirmOwnerMerge: true);
        var restored = merger.Unmerge(Seed.IdentityId);

        Assert.Equal(
            restored,
            db.Scalar<string>($"SELECT person_id FROM identity_person WHERE identity_id = '{Seed.IdentityId}';"));

        Assert.Equal("Sam", db.Scalar<string>($"SELECT display_name FROM person WHERE id = '{restored}';"));
        Assert.Equal(1, db.Scalar<long>("SELECT count(*) FROM person WHERE is_owner = 1;"));
    }

    /// <summary>
    /// The owner's own identity came from the export's personal_information, not from a guess.
    /// Detaching it would leave the archive with no definite "me" (P5).
    /// </summary>
    [Fact]
    public void The_owners_seed_identity_cannot_be_detached()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        var refused = Assert.Throws<InvalidOperationException>(() => merger.Unmerge(Seed.OwnerIdentityId));

        Assert.Contains("owner", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Seed.OwnerPersonId,
            db.Scalar<string>($"SELECT person_id FROM identity_person WHERE identity_id = '{Seed.OwnerIdentityId}';"));
    }

    [Fact]
    public void A_merge_followed_by_an_unmerge_restores_the_original_shape()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        var peopleBefore = db.Scalar<long>("SELECT count(*) FROM person;");

        merger.MergeInto(Seed.IdentityId, Seed.OwnerPersonId, confirmOwnerMerge: true);
        merger.Unmerge(Seed.IdentityId);

        Assert.Equal(peopleBefore, db.Scalar<long>("SELECT count(*) FROM person;"));
        Assert.Equal(1, db.Scalar<long>("SELECT count(*) FROM message;"));
    }

    [Fact]
    public void Renaming_a_person_leaves_their_identities_alone()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        merger.Rename(Seed.SamPersonId, "Samantha Ruiz");

        Assert.Equal("Samantha Ruiz", db.Scalar<string>($"SELECT display_name FROM person WHERE id = '{Seed.SamPersonId}';"));
        Assert.Equal("Sam", db.Scalar<string>($"SELECT display_name FROM identity WHERE id = '{Seed.IdentityId}';"));
    }

    [Fact]
    public void Merging_an_unknown_identity_fails_clearly()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        Assert.Throws<InvalidOperationException>(() => merger.MergeInto("nope", Seed.SamPersonId));
        Assert.Throws<InvalidOperationException>(() => merger.MergeInto(Seed.IdentityId, "nobody"));
    }

    // Merging one person into another, which is what someone actually wants once they have
    // realized that two rows in the People list are one human.

    /// <summary>Every account moves at once, and the emptied person goes.</summary>
    [Fact]
    public void Merging_a_person_moves_all_of_their_accounts()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        AddSecondAccountFor(db, Seed.SamPersonId, "idn-vk-sam");

        // A third person, with one account, who turns out to be the same human.
        db.Execute($"""
            INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
            VALUES ('idn-gh-sam', 'hangouts', '9', 'Sam', '{Seed.ImportId}', '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('p:gh-sam', 'Sam', 0, '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
            VALUES ('idn-gh-sam', 'p:gh-sam', 'auto', '2020-01-01T00:00:00.0000000+00:00');
            """);

        merger.MergePeople(Seed.SamPersonId, "p:gh-sam");

        Assert.Equal(
            3,
            db.Scalar<long>("SELECT count(*) FROM identity_person WHERE person_id = 'p:gh-sam';"));

        // Nothing points at the emptied person any more, so it is gone rather than left as a
        // second, empty copy in the People list.
        Assert.Equal(
            0,
            db.Scalar<long>($"SELECT count(*) FROM person WHERE id = '{Seed.SamPersonId}';"));

        // Messages are untouched, which is what makes any of this reversible (§1).
        Assert.Equal(
            Seed.IdentityId,
            db.Scalar<string>("SELECT sender_identity_id FROM message WHERE uid = 'tg/100/1';"));
    }

    /// <summary>
    /// Merging a person into the owner needs the same confirmation as merging an account does.
    /// </summary>
    /// <remarks>
    /// More is at stake here, not less: this moves everything that person ever said in one go.
    /// </remarks>
    [Fact]
    public void Merging_a_person_into_the_owner_must_be_confirmed()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        Assert.Throws<InvalidOperationException>(
            () => merger.MergePeople(Seed.SamPersonId, Seed.OwnerPersonId));

        merger.MergePeople(Seed.SamPersonId, Seed.OwnerPersonId, confirmOwnerMerge: true);

        Assert.Equal(
            Seed.OwnerPersonId,
            db.Scalar<string>($"SELECT person_id FROM identity_person WHERE identity_id = '{Seed.IdentityId}';"));
    }

    /// <summary>
    /// The owner cannot be dissolved into a contact.
    /// </summary>
    /// <remarks>
    /// P5: a save has exactly one owner and everything §7 derives is oriented around them. Merging
    /// that person away leaves an archive with no subject, and the operation someone meant is the
    /// one in the other direction.
    /// </remarks>
    [Fact]
    public void The_owner_cannot_be_merged_into_someone_else()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        var error = Assert.Throws<InvalidOperationException>(
            () => merger.MergePeople(Seed.OwnerPersonId, Seed.SamPersonId, confirmOwnerMerge: true));

        Assert.Contains("owner", error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            1, db.Scalar<long>("SELECT count(*) FROM person WHERE is_owner = 1;"));
    }

    [Fact]
    public void Merging_a_person_into_themselves_does_nothing()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        merger.MergePeople(Seed.SamPersonId, Seed.SamPersonId);

        Assert.Equal(
            1,
            db.Scalar<long>($"SELECT count(*) FROM identity_person WHERE person_id = '{Seed.SamPersonId}';"));
    }

    [Fact]
    public void Merging_an_unknown_person_fails_clearly()
    {
        var (db, merger) = Fixture();
        using var _ = db;

        Assert.Throws<InvalidOperationException>(() => merger.MergePeople("nobody", Seed.SamPersonId));
        Assert.Throws<InvalidOperationException>(() => merger.MergePeople(Seed.SamPersonId, "nobody"));
    }

    private static void AddSecondAccountFor(TempDatabase db, string personId, string identityId) =>
        db.Execute($"""
            INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
            VALUES ('{identityId}', 'vk', '222', 'Sam', '{Seed.ImportId}', '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
            VALUES ('{identityId}', '{personId}', 'manual', '2020-01-01T00:00:00.0000000+00:00');
            """);
}
