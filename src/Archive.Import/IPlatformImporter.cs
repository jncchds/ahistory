namespace Archive.Import;

/// <summary>A conversation, as a platform describes it.</summary>
/// <param name="SourceThreadId">The platform's own id for the thread. Must be stable across exports.</param>
/// <param name="Kind">dm, group, channel or saved.</param>
/// <param name="Participants">
/// Who is in it, when the export says.
/// </param>
/// <remarks>
/// <para>
/// Participants were derived from senders alone, which is a different question: it answers "who
/// spoke", not "who was here". A group of eleven where three people ever typed is a group of
/// three as far as the archive is concerned, and a roster the export actually states — Hangouts
/// writes one — was being read and discarded.
/// </para>
/// <para>
/// Senders are still recorded on top of this, so a format with no roster loses nothing.
/// </para>
/// </remarks>
public sealed record NormalizedThread(
    string SourceThreadId,
    string Kind,
    string? Title,
    IReadOnlyList<NormalizedIdentity>? Participants = null)
{
    public IReadOnlyList<NormalizedIdentity> Members => Participants ?? [];
}

/// <summary>Receives what an importer reads, in the order it reads it.</summary>
/// <remarks>
/// A push interface rather than an <c>IEnumerable</c> because exports are streamed: a format that
/// can only be read front to back — and most can — would otherwise have to be buffered whole.
/// </remarks>
public interface IImportSink
{
    /// <summary>
    /// The account this export belongs to, when the export says.
    /// </summary>
    /// <remarks>
    /// §2: this is what seeds the owner. A format that never states it — most of the old ones —
    /// simply does not call this, and the owner is resolved another way.
    /// </remarks>
    void OnOwner(NormalizedIdentity owner);

    void OnThread(NormalizedThread thread);

    void OnMessage(NormalizedThread thread, NormalizedMessage message);
}

/// <summary>What an importer can tell about a folder before reading it.</summary>
/// <param name="Confidence">
/// How sure the importer is that this is its format. Only the best candidate is used, and
/// <see cref="ImportConfidence.None"/> means "not mine".
/// </param>
/// <param name="AccountId">
/// The account the export belongs to. Stated by the format, or — when
/// <paramref name="AccountIdIsGuess"/> is true — worked out from something weaker, such as a
/// folder name.
/// </param>
/// <param name="AccountName">A human name for that account, if stated.</param>
/// <param name="AccountIdIsGuess">
/// True when the format does not state its account and the id above was inferred or is a
/// placeholder. The difference matters: a guessed owner is offered to the user to correct, and is
/// recorded as a guess rather than as something the export said (§1's <c>is_synthetic</c>).
/// </param>
/// <param name="AccountCandidates">
/// Accounts the export mentions that could be the owner's, best first. What the UI pre-fills the
/// "which of these is you?" field with. Empty when the importer has nothing to offer.
/// </param>
/// <param name="FileCount">How many files the import would read.</param>
/// <param name="Note">Anything the user should know before importing — shown in the UI.</param>
public sealed record ImportDetection(
    ImportConfidence Confidence,
    string? AccountId = null,
    string? AccountName = null,
    int FileCount = 0,
    string? Note = null,
    bool AccountIdIsGuess = false,
    IReadOnlyList<string>? AccountCandidates = null)
{
    public static ImportDetection No { get; } = new(ImportConfidence.None);

    public IReadOnlyList<string> Candidates => AccountCandidates ?? [];
}

/// <summary>What the caller knows that the export does not say.</summary>
/// <param name="OwnerAccountId">
/// The account on this platform that belongs to the person the save is about, when the user has
/// told us.
/// </param>
/// <remarks>
/// Only formats that do not state their own account (VK, QIP, most of the old ones) have anything
/// to do with this. It exists because the alternative — inventing an owner and carrying on — is
/// what produces an archive that looks right and attributes half of it to a person who does not
/// exist.
/// </remarks>
public sealed record ImportOptions(string? OwnerAccountId = null)
{
    public static ImportOptions Default { get; } = new();
}

/// <summary>How sure an importer is that a folder is its format.</summary>
public enum ImportConfidence
{
    /// <summary>Not this format.</summary>
    None = 0,

    /// <summary>Looks plausible — the right file extensions, roughly the right shape.</summary>
    Possible = 1,

    /// <summary>The format identified itself: a known filename, a known header, a schema marker.</summary>
    Certain = 2,
}

/// <summary>
/// Reads one platform's export format.
/// </summary>
/// <remarks>
/// <para>
/// Every importer produces the same <see cref="NormalizedMessage"/>, so the schema, the dedupe
/// rules and the whole app downstream are unchanged by adding a platform. §2's build order calls
/// the second importer the thing that proves the schema was right — this interface is where that
/// gets tested.
/// </para>
/// <para>
/// <b>Importers refuse rather than guess.</b> A format that is only half understood must throw on
/// what it does not recognize, never skip it and never invent a value. An archive quietly missing
/// a third of its messages, or showing them attributed to the wrong person, is far worse than an
/// import that stops and says it did not understand line 4,812 — that one can be fixed.
/// </para>
/// </remarks>
public interface IPlatformImporter
{
    /// <summary>Short stable id used in thread and identity keys: <c>telegram</c>, <c>vk</c>.</summary>
    string Platform { get; }

    /// <summary>What to call it in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this importer recognizes the folder, without reading all of it.
    /// </summary>
    /// <remarks>
    /// Cheap by contract: it runs for every registered importer whenever someone points at a
    /// folder, so it looks at filenames and at most the first few kilobytes of one file.
    /// </remarks>
    ImportDetection Detect(string path);

    /// <summary>Reads the export, pushing everything it finds at the sink.</summary>
    /// <param name="options">
    /// What the caller knows and the export does not say — chiefly which account is the owner's.
    /// Null means "nothing to add", which is the whole story for a format that states its own.
    /// </param>
    void Read(string path, IImportSink sink, ImportOptions? options = null);
}
