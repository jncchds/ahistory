namespace Archive.Import;

/// <summary>A conversation, as a platform describes it.</summary>
/// <param name="SourceThreadId">The platform's own id for the thread. Must be stable across exports.</param>
/// <param name="Kind">dm, group, channel or saved.</param>
public sealed record NormalizedThread(string SourceThreadId, string Kind, string? Title);

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
/// <param name="AccountId">The account the export belongs to, if the format states it.</param>
/// <param name="AccountName">A human name for that account, if stated.</param>
/// <param name="FileCount">How many files the import would read.</param>
/// <param name="Note">Anything the user should know before importing — shown in the UI.</param>
public sealed record ImportDetection(
    ImportConfidence Confidence,
    string? AccountId = null,
    string? AccountName = null,
    int FileCount = 0,
    string? Note = null)
{
    public static ImportDetection No { get; } = new(ImportConfidence.None);
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
    void Read(string path, IImportSink sink);
}
