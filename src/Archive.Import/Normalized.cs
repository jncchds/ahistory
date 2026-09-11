namespace Archive.Import;

/// <summary>One platform account, as the importer saw it.</summary>
/// <param name="SourceIdentityId">
/// Platform id with any prefix stripped, or null when the export gave only a name.
/// </param>
/// <param name="IsSynthetic">
/// True when there was no id and the identity is keyed by display name alone. Such identities are
/// merge candidates, never dedupe anchors — two people can share a name (§2).
/// </param>
public sealed record NormalizedIdentity(
    string Platform,
    string? SourceIdentityId,
    string? Handle,
    string DisplayName,
    bool IsSynthetic);

/// <summary>One attachment on a message.</summary>
/// <param name="ExportPath">Path relative to the export folder, as written in the JSON.</param>
/// <param name="MissingReason">
/// Set when the export omitted the file itself. A normal state, not an import failure: the user
/// exported without media, and the message still belongs in the archive.
/// </param>
public sealed record NormalizedMedia(
    string ExportPath,
    string MediaKind,
    string? OriginalFilename,
    string? Mime,
    string? MissingReason,
    string? StickerEmoji,
    long? Width,
    long? Height,
    long? DurationSeconds)
{
    /// <summary>
    /// The file's bytes, for a format that carries them inline rather than beside the export.
    /// </summary>
    /// <remarks>
    /// An SMS backup holds every MMS picture as base64 inside its XML, so there is no file to find.
    /// When set, <see cref="ExportPath"/> is a description rather than a path, and the extension is
    /// taken from <see cref="OriginalFilename"/>.
    /// </remarks>
    public byte[]? Content { get; init; }
}

/// <summary>
/// One reaction bucket.
/// </summary>
/// <param name="ActorIdentity">
/// Null for reactors the export did not name. Telegram lists only recent reactors, so a
/// popular message yields some named rows plus one anonymous row carrying the remainder —
/// which keeps the counts summing to the real total.
/// </param>
public sealed record NormalizedReaction(
    string Emoji,
    string? CustomEmojiId,
    NormalizedIdentity? ActorIdentity,
    long Count,
    string? ReactedAtUtc);

/// <summary>A message ready to be committed, with every §2 trap already resolved.</summary>
public sealed record NormalizedMessage
{
    public required string Uid { get; init; }
    public required string SourceThreadId { get; init; }
    public required string Kind { get; init; }
    public NormalizedIdentity? Sender { get; init; }
    public string? ServiceAction { get; init; }
    public required string SentAtUtc { get; init; }
    public required long SentAtUnix { get; init; }
    public long? TzOffsetMinutes { get; init; }
    public required string Plaintext { get; init; }
    public string? EntitiesJson { get; init; }
    public required string ContentHash { get; init; }
    public string? ReplyToUid { get; init; }
    public string? ForwardedFrom { get; init; }
    public string? ForwardedAtUtc { get; init; }
    public string? ViaBot { get; init; }
    public string? EditedAtUtc { get; init; }
    public string? RawJson { get; init; }
    public IReadOnlyList<NormalizedMedia> Media { get; init; } = [];
    public IReadOnlyList<NormalizedReaction> Reactions { get; init; } = [];
}
