using Microsoft.EntityFrameworkCore;

namespace Archive.Data;

/// <summary>
/// EF Core mapping over the schema that <see cref="Database"/> migrated.
/// </summary>
/// <remarks>
/// <para>
/// EF is a mapper here, not the schema authority (decisions.md D3). Consequences that matter:
/// </para>
/// <list type="bullet">
///   <item><c>EnsureCreated</c> is never called, and there are no EF migrations.</item>
///   <item>Virtual tables (<c>search_fts</c>) are invisible to EF and are queried with raw SQL.</item>
///   <item>Tracking is off by default: V1's EF usage is entirely read-only, and the write paths
///   use prepared SqliteCommands because 500k rows through the change tracker is minutes
///   instead of seconds.</item>
/// </list>
/// </remarks>
public sealed class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
    public DbSet<SaveMeta> SaveMeta => Set<SaveMeta>();
    public DbSet<Import> Imports => Set<Import>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Identity> Identities => Set<Identity>();
    public DbSet<IdentityPerson> IdentityPersons => Set<IdentityPerson>();
    public DbSet<Thread> Threads => Set<Thread>();
    public DbSet<ThreadParticipant> ThreadParticipants => Set<ThreadParticipant>();
    public DbSet<Media> Media => Set<Media>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageImport> MessageImports => Set<MessageImport>();
    public DbSet<MessageRevision> MessageRevisions => Set<MessageRevision>();
    public DbSet<Reaction> Reactions => Set<Reaction>();
    public DbSet<MessageMedia> MessageMedia => Set<MessageMedia>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);

        b.Entity<SaveMeta>(e =>
        {
            e.ToTable("save_meta");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OwnerIsSelf).HasColumnName("owner_is_self");
            e.Property(x => x.Provenance).HasColumnName("provenance");
            e.Property(x => x.CreatedUtc).HasColumnName("created_utc");
        });

        b.Entity<Import>(e =>
        {
            e.ToTable("import");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Platform).HasColumnName("platform");
            e.Property(x => x.SourcePath).HasColumnName("source_path");
            e.Property(x => x.SourceFingerprint).HasColumnName("source_fingerprint");
            e.Property(x => x.ImporterVersion).HasColumnName("importer_version");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.StartedUtc).HasColumnName("started_utc");
            e.Property(x => x.FinishedUtc).HasColumnName("finished_utc");
            e.Property(x => x.StatsJson).HasColumnName("stats_json");
            e.Property(x => x.LastError).HasColumnName("last_error");
        });

        b.Entity<Person>(e =>
        {
            e.ToTable("person");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.IsOwner).HasColumnName("is_owner");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CreatedUtc).HasColumnName("created_utc");
        });

        b.Entity<Identity>(e =>
        {
            e.ToTable("identity");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Platform).HasColumnName("platform");
            e.Property(x => x.SourceIdentityId).HasColumnName("source_identity_id");
            e.Property(x => x.Handle).HasColumnName("handle");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.IsSynthetic).HasColumnName("is_synthetic");
            e.Property(x => x.FirstImportId).HasColumnName("first_import_id");
            e.Property(x => x.CreatedUtc).HasColumnName("created_utc");
        });

        b.Entity<IdentityPerson>(e =>
        {
            e.ToTable("identity_person");
            e.HasKey(x => x.IdentityId);
            e.Property(x => x.IdentityId).HasColumnName("identity_id");
            e.Property(x => x.PersonId).HasColumnName("person_id");
            e.Property(x => x.Confidence).HasColumnName("confidence");
            e.Property(x => x.LinkedUtc).HasColumnName("linked_utc");
        });

        b.Entity<Thread>(e =>
        {
            e.ToTable("thread");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Platform).HasColumnName("platform");
            e.Property(x => x.SourceThreadId).HasColumnName("source_thread_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.FirstImportId).HasColumnName("first_import_id");
            e.Property(x => x.CreatedUtc).HasColumnName("created_utc");
        });

        b.Entity<ThreadParticipant>(e =>
        {
            e.ToTable("thread_participant");
            e.HasKey(x => new { x.ThreadId, x.IdentityId });
            e.Property(x => x.ThreadId).HasColumnName("thread_id");
            e.Property(x => x.IdentityId).HasColumnName("identity_id");
            e.Property(x => x.FirstSeenUnix).HasColumnName("first_seen_unix");
        });

        b.Entity<Media>(e =>
        {
            e.ToTable("media");
            e.HasKey(x => x.Hash);
            e.Property(x => x.Hash).HasColumnName("hash");
            e.Property(x => x.ByteSize).HasColumnName("byte_size");
            e.Property(x => x.Mime).HasColumnName("mime");
            e.Property(x => x.Extension).HasColumnName("extension");
            e.Property(x => x.MediaKind).HasColumnName("media_kind");
            e.Property(x => x.Width).HasColumnName("width");
            e.Property(x => x.Height).HasColumnName("height");
            e.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
            e.Property(x => x.FirstImportId).HasColumnName("first_import_id");
            e.Property(x => x.CreatedUtc).HasColumnName("created_utc");
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("session");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ThreadId).HasColumnName("thread_id");
            e.Property(x => x.StartedAtUnix).HasColumnName("started_at_unix");
            e.Property(x => x.EndedAtUnix).HasColumnName("ended_at_unix");
            e.Property(x => x.MessageCount).HasColumnName("message_count");
            e.Property(x => x.MemberHash).HasColumnName("member_hash");
            e.Property(x => x.SegmenterVersion).HasColumnName("segmenter_version");
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("message");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Uid).HasColumnName("uid");
            e.Property(x => x.ThreadId).HasColumnName("thread_id");
            e.Property(x => x.SenderIdentityId).HasColumnName("sender_identity_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.ServiceAction).HasColumnName("service_action");
            e.Property(x => x.SentAtUtc).HasColumnName("sent_at_utc");
            e.Property(x => x.SentAtUnix).HasColumnName("sent_at_unix");
            e.Property(x => x.TzOffsetMinutes).HasColumnName("tz_offset_minutes");
            e.Property(x => x.Plaintext).HasColumnName("plaintext");
            e.Property(x => x.EntitiesJson).HasColumnName("entities_json");
            e.Property(x => x.ContentHash).HasColumnName("content_hash");
            e.Property(x => x.ReplyToUid).HasColumnName("reply_to_uid");
            e.Property(x => x.ForwardedFrom).HasColumnName("forwarded_from");
            e.Property(x => x.ForwardedAtUtc).HasColumnName("forwarded_at_utc");
            e.Property(x => x.ViaBot).HasColumnName("via_bot");
            e.Property(x => x.EditedAtUtc).HasColumnName("edited_at_utc");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.RawJson).HasColumnName("raw_json");
            e.Property(x => x.FirstImportId).HasColumnName("first_import_id");
            e.Property(x => x.ImporterVersion).HasColumnName("importer_version");
        });

        b.Entity<MessageImport>(e =>
        {
            e.ToTable("message_import");
            e.HasKey(x => new { x.MessageId, x.ImportId });
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.ImportId).HasColumnName("import_id");
            e.Property(x => x.IsFirst).HasColumnName("is_first");
            e.Property(x => x.SeenUtc).HasColumnName("seen_utc");
        });

        b.Entity<MessageRevision>(e =>
        {
            e.ToTable("message_revision");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.ObservedImportId).HasColumnName("observed_import_id");
            e.Property(x => x.Plaintext).HasColumnName("plaintext");
            e.Property(x => x.EntitiesJson).HasColumnName("entities_json");
            e.Property(x => x.ContentHash).HasColumnName("content_hash");
            e.Property(x => x.RawJson).HasColumnName("raw_json");
            e.Property(x => x.ObservedUtc).HasColumnName("observed_utc");
        });

        b.Entity<Reaction>(e =>
        {
            e.ToTable("reaction");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.Emoji).HasColumnName("emoji");
            e.Property(x => x.CustomEmojiId).HasColumnName("custom_emoji_id");
            e.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id");
            e.Property(x => x.Count).HasColumnName("count");
            e.Property(x => x.ReactedAtUtc).HasColumnName("reacted_at_utc");
        });

        b.Entity<MessageMedia>(e =>
        {
            e.ToTable("message_media");
            e.HasKey(x => new { x.MessageId, x.Ordinal });
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.MediaHash).HasColumnName("media_hash");
            e.Property(x => x.ExportPath).HasColumnName("export_path");
            e.Property(x => x.OriginalFilename).HasColumnName("original_filename");
            e.Property(x => x.MissingReason).HasColumnName("missing_reason");
            e.Property(x => x.StickerEmoji).HasColumnName("sticker_emoji");
        });
    }
}
