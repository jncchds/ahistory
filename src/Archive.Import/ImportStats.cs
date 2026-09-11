namespace Archive.Import;

/// <summary>
/// What an import actually did.
/// </summary>
/// <remarks>
/// Recorded on the import row as JSON. For a clean re-import this reads
/// <c>inserted 0, skipped == seen</c>, which is the fastest human-readable proof that the
/// importer is idempotent — no digest required, just look at the numbers.
/// </remarks>
public sealed record ImportStats
{
    public long MessagesSeen { get; set; }
    public long MessagesInserted { get; set; }
    public long MessagesSkipped { get; set; }
    public long MessagesRevised { get; set; }
    public long ThreadsNew { get; set; }
    public long IdentitiesNew { get; set; }
    public long MediaStored { get; set; }
    public long MediaDeduplicated { get; set; }
    public long MediaMissing { get; set; }
    public long MediaNotFound { get; set; }

    /// <summary>Stored messages the platform reported deleted during this run. Kept, and marked (P2).</summary>
    public long MessagesDeleted { get; set; }

    public override string ToString() =>
        $"seen {MessagesSeen}, inserted {MessagesInserted}, skipped {MessagesSkipped}, "
        + $"revised {MessagesRevised}, threads +{ThreadsNew}, identities +{IdentitiesNew}, "
        + $"media stored {MediaStored} / deduped {MediaDeduplicated} / missing {MediaMissing}"
        + (MessagesDeleted > 0 ? $", deleted on the platform {MessagesDeleted}" : string.Empty);
}
