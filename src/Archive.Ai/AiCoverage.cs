using Archive.Ai.Extraction;
using Archive.Ai.Sessions;
using Archive.Data;

namespace Archive.Ai;

/// <summary>
/// How much of an archive has been read, and at what version.
/// </summary>
/// <param name="Threads">Conversations in the archive.</param>
/// <param name="ThreadsSegmented">Conversations that have been split into sessions.</param>
/// <param name="Messages">Messages that can belong to a session — not service, not deleted.</param>
/// <param name="MessagesInSessions">Messages that do.</param>
/// <param name="Sessions">Sessions at the current segmenter version.</param>
/// <param name="Substantive">
/// Sessions the cheap filter thinks carry something (§6.2). The rest are logistics, and skipping
/// them is where most of the token bill goes away.
/// </param>
/// <param name="Extracted">
/// Substantive sessions a model has read at the current prompt version. "Read under an older
/// prompt" is not counted, because it is work that has to be done again.
/// </param>
public sealed record CoverageSummary(
    long Threads,
    long ThreadsSegmented,
    long Messages,
    long MessagesInSessions,
    long Sessions,
    long Substantive,
    long Extracted)
{
    public static CoverageSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>True once every thread has been segmented at the current version.</summary>
    public bool IsSegmented => Threads > 0 && ThreadsSegmented >= Threads;

    /// <summary>Fraction of threads segmented, 0 to 1.</summary>
    public double SegmentedFraction => Threads == 0 ? 0 : (double)ThreadsSegmented / Threads;

    /// <summary>Fraction of the sessions worth reading that have been read, 0 to 1.</summary>
    public double ExtractedFraction => Substantive == 0 ? 0 : (double)Extracted / Substantive;
}

/// <summary>
/// The counts behind "how much of this has been read".
/// </summary>
/// <remarks>
/// <para>
/// "Processed" is not a boolean; it is a boolean <b>at a version</b>. Everything here is counted
/// against the segmenter and prompt versions this build carries, so bumping either drops coverage
/// rather than leaving a page reporting work done under rules that no longer apply.
/// </para>
/// <para>
/// Every query is a count over indexed columns, because this is read on a page the user opens
/// while a drain is running and it must not compete with it.
/// </para>
/// </remarks>
public sealed class AiCoverage(Database database)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Coverage of the whole save.</summary>
    public CoverageSummary Summary()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
                (SELECT count(DISTINCT thread_id) FROM message
                 WHERE kind = 'message' AND is_deleted = 0),
                (SELECT count(DISTINCT thread_id) FROM session WHERE segmenter_version = $version),
                (SELECT count(*) FROM message WHERE kind = 'message' AND is_deleted = 0),
                (SELECT count(*) FROM message
                 WHERE kind = 'message' AND is_deleted = 0 AND session_id IS NOT NULL),
                (SELECT count(*) FROM session WHERE segmenter_version = $version),
                (SELECT count(*) FROM session
                 WHERE segmenter_version = $version AND is_substantive = 1),
                (SELECT count(*) FROM session AS s
                 WHERE s.segmenter_version = $version AND s.is_substantive = 1
                   AND EXISTS (
                       SELECT 1 FROM derived_artifact AS d
                       WHERE d.source_session_id = s.id
                         AND d.kind = 'session_extract'
                         AND d.prompt_version = $prompt));
            """;

        command.Parameters.AddWithValue("$version", SessionSegmenter.Version);
        command.Parameters.AddWithValue("$prompt", PromptCatalog.VersionOf(PromptCatalog.ExtractSession));

        using var reader = command.ExecuteReader();

        return reader.Read() ? Read(reader) : CoverageSummary.Empty;
    }

    /// <summary>
    /// Coverage of one person's conversations.
    /// </summary>
    /// <remarks>
    /// A person's threads, not a person's sessions: sessions belong to threads (§6.1), and the
    /// per-person view composes them. Someone who appears in a busy group therefore shares its
    /// coverage with everyone else in the room, which is the honest answer — the group was read
    /// once, for all of them.
    /// </remarks>
    public CoverageSummary ForPerson(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            WITH mine AS (
                SELECT DISTINCT tp.thread_id
                FROM thread_participant AS tp
                JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
                WHERE ip.person_id = $person
            )
            SELECT
                (SELECT count(*) FROM mine),
                (SELECT count(DISTINCT s.thread_id) FROM session AS s
                 JOIN mine ON mine.thread_id = s.thread_id
                 WHERE s.segmenter_version = $version),
                (SELECT count(*) FROM message AS m JOIN mine ON mine.thread_id = m.thread_id
                 WHERE m.kind = 'message' AND m.is_deleted = 0),
                (SELECT count(*) FROM message AS m JOIN mine ON mine.thread_id = m.thread_id
                 WHERE m.kind = 'message' AND m.is_deleted = 0 AND m.session_id IS NOT NULL),
                (SELECT count(*) FROM session AS s JOIN mine ON mine.thread_id = s.thread_id
                 WHERE s.segmenter_version = $version),
                (SELECT count(*) FROM session AS s JOIN mine ON mine.thread_id = s.thread_id
                 WHERE s.segmenter_version = $version AND s.is_substantive = 1),
                (SELECT count(*) FROM session AS s JOIN mine ON mine.thread_id = s.thread_id
                 WHERE s.segmenter_version = $version AND s.is_substantive = 1
                   AND EXISTS (
                       SELECT 1 FROM derived_artifact AS d
                       WHERE d.source_session_id = s.id
                         AND d.kind = 'session_extract'
                         AND d.prompt_version = $prompt));
            """;

        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$version", SessionSegmenter.Version);
        command.Parameters.AddWithValue("$prompt", PromptCatalog.VersionOf(PromptCatalog.ExtractSession));

        using var reader = command.ExecuteReader();

        return reader.Read() ? Read(reader) : CoverageSummary.Empty;
    }

    private static CoverageSummary Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Threads: reader.GetInt64(0),
        ThreadsSegmented: reader.GetInt64(1),
        Messages: reader.GetInt64(2),
        MessagesInSessions: reader.GetInt64(3),
        Sessions: reader.GetInt64(4),
        Substantive: reader.GetInt64(5),
        Extracted: reader.GetInt64(6));
}
