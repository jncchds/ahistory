namespace Archive.Data.Tests;

/// <summary>
/// Minimal rows for tests that need a message to exist before they can assert anything about it.
/// </summary>
/// <remarks>
/// Deliberately hand-written SQL rather than going through the importer: these tests check what
/// the schema and its triggers do, and routing them through the importer would mean a schema bug
/// could be masked by an importer bug, or vice versa.
/// </remarks>
internal static class Seed
{
    internal const string SourceId = "telegram:account:777001";
    internal const string ImportId = "imp-1";
    internal const string ThreadId = "thr-1";
    internal const string OtherThreadId = "thr-2";
    internal const string IdentityId = "idn-1";

    /// <summary>Creates one import, two threads and one identity.</summary>
    internal static void Basics(TempDatabase db) => db.Execute($"""
        INSERT INTO import_source (id, platform, label, created_utc)
        VALUES ('{SourceId}', 'telegram', 'Test account', '2020-01-01T00:00:00.0000000+00:00');

        INSERT INTO import (id, source_id, platform, source_path, source_fingerprint, importer_version, status, started_utc)
        VALUES ('{ImportId}', '{SourceId}', 'telegram', '/tmp/export', 'fp', 'test', 'completed', '2020-01-01T00:00:00.0000000+00:00');

        INSERT INTO thread (id, platform, source_thread_id, kind, title, first_import_id, created_utc)
        VALUES ('{ThreadId}', 'telegram', '100', 'dm', 'Sam', '{ImportId}', '2020-01-01T00:00:00.0000000+00:00'),
               ('{OtherThreadId}', 'telegram', '200', 'group', 'Trip', '{ImportId}', '2020-01-01T00:00:00.0000000+00:00');

        INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
        VALUES ('{IdentityId}', 'telegram', '5', 'Sam', '{ImportId}', '2020-01-01T00:00:00.0000000+00:00');
        """);

    internal const string OwnerPersonId = "owner";
    internal const string OwnerIdentityId = "idn-owner";
    internal const string SamPersonId = "p:idn-1";

    /// <summary>
    /// Adds an owner and a contact, each with one identity, as the importer would have.
    /// </summary>
    internal static void People(TempDatabase db) => db.Execute($"""
        INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
        VALUES ('{OwnerIdentityId}', 'telegram', '777001', 'Kirill', '{ImportId}', '2020-01-01T00:00:00.0000000+00:00');

        INSERT INTO person (id, display_name, is_owner, created_utc)
        VALUES ('{OwnerPersonId}', 'Kirill', 1, '2020-01-01T00:00:00.0000000+00:00'),
               ('{SamPersonId}', 'Sam', 0, '2020-01-01T00:00:00.0000000+00:00');

        INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
        VALUES ('{OwnerIdentityId}', '{OwnerPersonId}', 'seed', '2020-01-01T00:00:00.0000000+00:00'),
               ('{IdentityId}', '{SamPersonId}', 'auto', '2020-01-01T00:00:00.0000000+00:00');
        """);

    /// <summary>Inserts one message and returns nothing — tests look it up by uid.</summary>
    internal static void Message(TempDatabase db, string uid, string plaintext, string threadId = ThreadId) =>
        db.Execute($"""
            INSERT INTO message (uid, thread_id, sender_identity_id, kind, sent_at_utc, sent_at_unix,
                                 plaintext, content_hash, first_import_id, importer_version)
            VALUES ('{uid}', '{threadId}', '{IdentityId}', 'message',
                    '2020-01-01T12:00:00.0000000+00:00', 1577880000,
                    '{plaintext}', 'hash-{uid}', '{ImportId}', 'test');
            """);
}
