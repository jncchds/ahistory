namespace Archive.Import;

/// <summary>An existing source an export could be attributed to.</summary>
/// <param name="Id">Stable id, e.g. <c>telegram:account:777001</c>.</param>
/// <param name="Label">What the user calls it, if they renamed it.</param>
/// <param name="MessageCount">How many messages already belong to it.</param>
/// <param name="ImportCount">How many runs it has had.</param>
/// <param name="LastImportedUtc">When it was last run, or null if never completed.</param>
/// <param name="IsSuggested">
/// True when the export's own contents identify it as this source — the same account, not a
/// guess from overlap.
/// </param>
public sealed record ImportSourceOption(
    string Id,
    string? Label,
    long MessageCount,
    long ImportCount,
    string? LastImportedUtc,
    bool IsSuggested)
{
    public string DisplayName => Label ?? Id;
}

/// <summary>
/// What an export folder looks like before anything is written, so the user can be asked where
/// it belongs.
/// </summary>
/// <remarks>
/// Auto-detection suggests, it does not decide. Only the user knows whether a folder is a fresh
/// export of their own account or an archive someone handed them that happens to overlap, and
/// getting that wrong merges two people's provenance in a way that is tedious to unpick.
/// </remarks>
public sealed record ImportPreview
{
    public required string ExportFolder { get; init; }
    public required string Platform { get; init; }
    public required int FileCount { get; init; }

    /// <summary>The account the export identifies itself as belonging to, when it says.</summary>
    public string? DetectedAccountId { get; init; }

    public string? DetectedAccountName { get; init; }

    /// <summary>The owner this save already has, if any.</summary>
    public string? OwnerName { get; init; }

    /// <summary>
    /// True when the export names an account that is not yet one of the owner's.
    /// </summary>
    /// <remarks>
    /// A save is one person's archive (decisions.md D13), and the importer acts on that: a new
    /// account is attached to the existing owner, on the assumption it is another of their
    /// accounts. That is right for a work and a personal Telegram, and wrong for someone else's
    /// archive — and the difference is invisible to the importer, so it is surfaced here for the
    /// user to notice before it is merged into their own identity.
    /// </remarks>
    public bool AccountIsNewToOwner { get; init; }

    /// <summary>Every source already in the save, most-populated first.</summary>
    public required IReadOnlyList<ImportSourceOption> ExistingSources { get; init; }

    /// <summary>
    /// Where this export will go unless the user says otherwise.
    /// </summary>
    public required string SuggestedSourceId { get; init; }

    /// <summary>False when the suggestion is to create a new source rather than extend one.</summary>
    public required bool SuggestedSourceExists { get; init; }

    public string? SuggestedLabel { get; init; }

    /// <summary>
    /// Why the suggestion was made, in words the UI can show next to it.
    /// </summary>
    public required string SuggestionReason { get; init; }
}
