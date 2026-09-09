using System.Text;
using Archive.Data;

namespace Archive.Import.Tests;

/// <summary>
/// A stable text rendering of everything an import should have produced.
/// </summary>
/// <remarks>
/// <para>
/// Comparing digests instead of counts means a failure names the row that diverged rather than
/// reporting that two numbers differ. Rows are ordered explicitly, and messages are identified by
/// uid rather than by rowid, so the digest describes the archive's content and not its storage.
/// </para>
/// <para>
/// Deliberately excluded: <c>import</c> and <c>message_import</c>, and every <c>first_import_id</c>
/// and timestamp column. Those legitimately change when the same export is imported a second
/// time — a second import genuinely happened — while the archive's content must not. Keeping them
/// out is what lets the idempotency test assert the strong claim rather than a weakened one.
/// </para>
/// </remarks>
internal static class DatabaseDigest
{
    private static readonly (string Label, string Sql)[] Sections =
    [
        ("person", """
            SELECT id, display_name, is_owner FROM person ORDER BY id;
            """),
        ("identity", """
            SELECT id, platform, ifnull(source_identity_id, '-'), ifnull(handle, '-'),
                   display_name, is_synthetic
            FROM identity ORDER BY id;
            """),
        ("identity_person", """
            SELECT identity_id, person_id, confidence FROM identity_person ORDER BY identity_id;
            """),
        ("thread", """
            SELECT id, platform, source_thread_id, kind, ifnull(title, '-') FROM thread ORDER BY id;
            """),
        ("thread_participant", """
            SELECT thread_id, identity_id, first_seen_unix
            FROM thread_participant ORDER BY thread_id, identity_id;
            """),
        ("media", """
            SELECT hash, byte_size, ifnull(mime, '-'), ifnull(extension, '-'), media_kind,
                   ifnull(width, -1), ifnull(height, -1), ifnull(duration_seconds, -1)
            FROM media ORDER BY hash;
            """),
        ("message", """
            SELECT uid, thread_id, ifnull(sender_identity_id, '-'), kind, ifnull(service_action, '-'),
                   sent_at_utc, sent_at_unix, ifnull(tz_offset_minutes, -1), plaintext,
                   ifnull(entities_json, '-'), content_hash, ifnull(reply_to_uid, '-'),
                   ifnull(forwarded_from, '-'), ifnull(via_bot, '-'), ifnull(edited_at_utc, '-'),
                   is_deleted, importer_version
            FROM message ORDER BY uid;
            """),
        ("message_media", """
            SELECT m.uid, mm.ordinal, ifnull(mm.media_hash, '-'), mm.export_path,
                   ifnull(mm.original_filename, '-'), ifnull(mm.missing_reason, '-'),
                   ifnull(mm.sticker_emoji, '-')
            FROM message_media mm JOIN message m ON m.id = mm.message_id
            ORDER BY m.uid, mm.ordinal;
            """),
        ("reaction", """
            SELECT m.uid, r.emoji, ifnull(r.custom_emoji_id, '-'), ifnull(r.actor_identity_id, '-'), r.count
            FROM reaction r JOIN message m ON m.id = r.message_id
            ORDER BY m.uid, r.emoji, ifnull(r.actor_identity_id, '');
            """),
        ("message_revision", """
            SELECT m.uid, rev.plaintext, rev.content_hash
            FROM message_revision rev JOIN message m ON m.id = rev.message_id
            ORDER BY m.uid, rev.content_hash;
            """),
        ("search_document", """
            SELECT m.uid, sd.provenance, sd.body
            FROM search_document sd JOIN message m ON m.id = sd.message_id
            ORDER BY m.uid;
            """),
    ];

    internal static string Of(Database database)
    {
        using var connection = database.Open();
        var builder = new StringBuilder();

        foreach (var (label, sql) in Sections)
        {
            builder.Append("== ").Append(label).Append('\n');

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(" | ");
                    }

                    builder.Append(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString());
                }

                builder.Append('\n');
            }
        }

        return builder.ToString();
    }
}
