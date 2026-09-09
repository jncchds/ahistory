namespace Archive.Data;

// Entities for the tables V1 reads. The SQL in Migrations/ is the authority for the schema
// (decisions.md D3); these types are a mapping over it, and EfSchemaTests proves the two agree.
//
// Derived-artifact and fact tables are deliberately unmapped: nothing in V1 reads them, and an
// unused entity is a place for drift to hide.

public sealed class SaveMeta
{
    public long Id { get; set; }
    public bool OwnerIsSelf { get; set; }
    public string? Provenance { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
}

public sealed class Import
{
    public string Id { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string ImporterVersion { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StartedUtc { get; set; } = string.Empty;
    public string? FinishedUtc { get; set; }
    public string? StatsJson { get; set; }
    public string? LastError { get; set; }
}

public sealed class Person
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsOwner { get; set; }
    public string? Notes { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
}

public sealed class Identity
{
    public string Id { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? SourceIdentityId { get; set; }
    public string? Handle { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsSynthetic { get; set; }
    public string FirstImportId { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
}

public sealed class IdentityPerson
{
    public string IdentityId { get; set; } = string.Empty;
    public string PersonId { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public string LinkedUtc { get; set; } = string.Empty;
}

public sealed class Thread
{
    public string Id { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string SourceThreadId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string FirstImportId { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
}

public sealed class ThreadParticipant
{
    public string ThreadId { get; set; } = string.Empty;
    public string IdentityId { get; set; } = string.Empty;
    public long FirstSeenUnix { get; set; }
}

public sealed class Media
{
    public string Hash { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public string? Mime { get; set; }
    public string MediaKind { get; set; } = string.Empty;
    public long? Width { get; set; }
    public long? Height { get; set; }
    public long? DurationSeconds { get; set; }
    public string FirstImportId { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
}

public sealed class Session
{
    public string Id { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public long StartedAtUnix { get; set; }
    public long EndedAtUnix { get; set; }
    public long MessageCount { get; set; }
    public string MemberHash { get; set; } = string.Empty;
    public string SegmenterVersion { get; set; } = string.Empty;
}

public sealed class Message
{
    public long Id { get; set; }
    public string Uid { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string? SenderIdentityId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? ServiceAction { get; set; }
    public string SentAtUtc { get; set; } = string.Empty;
    public long SentAtUnix { get; set; }
    public long? TzOffsetMinutes { get; set; }
    public string Plaintext { get; set; } = string.Empty;
    public string? EntitiesJson { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string? ReplyToUid { get; set; }
    public string? ForwardedFrom { get; set; }
    public string? ForwardedAtUtc { get; set; }
    public string? ViaBot { get; set; }
    public string? EditedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public string? SessionId { get; set; }
    public string? RawJson { get; set; }
    public string FirstImportId { get; set; } = string.Empty;
    public string ImporterVersion { get; set; } = string.Empty;
}

public sealed class MessageRevision
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string ObservedImportId { get; set; } = string.Empty;
    public string Plaintext { get; set; } = string.Empty;
    public string? EntitiesJson { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string? RawJson { get; set; }
    public string ObservedUtc { get; set; } = string.Empty;
}

public sealed class Reaction
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public string? CustomEmojiId { get; set; }
    public string? ActorIdentityId { get; set; }
    public long Count { get; set; }
    public string? ReactedAtUtc { get; set; }
}

public sealed class MessageMedia
{
    public long MessageId { get; set; }
    public long Ordinal { get; set; }
    public string? MediaHash { get; set; }
    public string ExportPath { get; set; } = string.Empty;
    public string? OriginalFilename { get; set; }
    public string? MissingReason { get; set; }
    public string? StickerEmoji { get; set; }
}
