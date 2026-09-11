using Archive.Ai.Attachments;
using Archive.Ai.Diary;
using Archive.Ai.Extraction;
using Archive.Ai.Merging;
using Archive.Ai.Search;
using Archive.Ai.Sessions;
using Archive.Data;

namespace Archive.Ai.Jobs;

/// <summary>
/// Turns "something changed" into jobs.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to the runner: work is enqueued by invalidation, never by a button
/// (ai-plan.md §11.1). Enabling AI, finishing an import and opening a save all ask the same
/// question — what is out of date? — and the answer is a set of jobs and nothing else.
/// </para>
/// <para>
/// Everything here is cheap enough to run on every start. A thread whose messages have not
/// changed produces the same input hash, the queue recognizes the job it already has, and nothing
/// is written at all.
/// </para>
/// </remarks>
public sealed class AiWork(
    Database database,
    AiJobs jobs,
    SessionSegmenter segmenter,
    AiRunner runner,
    FactMerger? merger = null,
    DiaryPlanner? diary = null,
    EmbeddingStore? embeddings = null,
    AiState? state = null,
    MediaReader? media = null)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    private readonly AiJobs _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));

    private readonly SessionSegmenter _segmenter =
        segmenter ?? throw new ArgumentNullException(nameof(segmenter));

    private readonly AiRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>
    /// Queues segmentation for every thread that needs it.
    /// </summary>
    /// <remarks>
    /// Priority is the thread's last activity in whole days, so the queue drains newest first. A
    /// ten-year archive worked through oldest-first shows nothing recognisable for a long time,
    /// and a user watching that cannot tell it from a hang.
    /// </remarks>
    /// <returns>How many threads were left with something to do.</returns>
    public int PlanSegmentation()
    {
        var queued = 0;

        foreach (var (threadId, lastUnix, messages) in _segmenter.Threads())
        {
            var enqueued = _jobs.Enqueue(
                AiJobKind.Segment,
                subjectKind: "thread",
                subjectId: threadId,
                inputHash: _segmenter.InputHash(threadId, lastUnix, messages),
                priority: (int)(lastUnix / 86_400));

            if (enqueued)
            {
                queued++;
            }
        }

        if (queued > 0)
        {
            _runner.Poke();
        }

        return queued;
    }

    /// <summary>
    /// Queues extraction for every session worth reading that has not been read at this prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only sessions the cheap filter kept (§6.2), and only ones with no extract at the current
    /// prompt version. The input hash is the session's own membership hash plus the versions, so a
    /// session that gained a message — and therefore has a new identity entirely — is new work,
    /// and one that did not is not.
    /// </para>
    /// <para>
    /// Newest first, like segmentation: the facts panel should have something in it within
    /// minutes, for a conversation the user remembers.
    /// </para>
    /// </remarks>
    /// <returns>How many sessions were left with something to do.</returns>
    public int PlanExtraction()
    {
        var promptVersion = PromptCatalog.VersionOf(PromptCatalog.ExtractSession);
        var queued = 0;

        foreach (var (sessionId, memberHash, endedAt) in Unread(promptVersion))
        {
            var enqueued = _jobs.Enqueue(
                AiJobKind.Extract,
                subjectKind: "session",
                subjectId: sessionId,
                inputHash: $"{memberHash}|{promptVersion}",
                priority: (int)(endedAt / 86_400));

            if (enqueued)
            {
                queued++;
            }
        }

        if (queued > 0)
        {
            _runner.Poke();
        }

        return queued;
    }

    /// <summary>
    /// Queues merging for every person with facts that might be one fact (A4).
    /// </summary>
    /// <remarks>
    /// Lowest priority: merging tidies what extraction wrote, and doing it while extraction is still
    /// writing means doing it again. Queued last, it runs once the newest conversations have been
    /// read and before the diary, which waits for it.
    /// </remarks>
    public int PlanMerging()
    {
        if (merger is null)
        {
            return 0;
        }

        var queued = 0;

        foreach (var personId in merger.PeopleWithCandidates())
        {
            var plan = merger.Plan(personId);

            if (!plan.IsEmpty && _jobs.Enqueue(AiJobKind.Adjudicate, "person", personId, plan.InputHash))
            {
                queued++;
            }
        }

        if (queued > 0)
        {
            _runner.Poke();
        }

        return queued;
    }

    /// <summary>
    /// Everything that sends archive text to a model, in the order it depends on itself.
    /// </summary>
    /// <remarks>
    /// What runs after every pass of the runner once the endpoint has been agreed to: reading a
    /// session makes facts, facts make merges, and so on up. Each step is idempotent, so asking
    /// again when nothing changed queues nothing.
    /// </remarks>
    public int PlanAll() => PlanExtraction() + PlanMerging() + PlanDiary() + PlanEmbeddings() + PlanMedia();

    /// <summary>
    /// Queues reading for images and voice messages not yet read with the configured models (A7).
    /// </summary>
    /// <remarks>
    /// Each only when its model is set. Neither is on by default: every photo and every voice note
    /// is a call, and both are often more private than the messages around them.
    /// </remarks>
    public int PlanMedia()
    {
        if (media is null || state is null)
        {
            return 0;
        }

        var settings = state.Current;
        var queued = 0;

        if (!string.IsNullOrWhiteSpace(settings.VisionModel))
        {
            queued += media.Needing(AiJobKind.Ocr, settings.VisionModel.Trim()).Count(file =>
                _jobs.Enqueue(AiJobKind.Ocr, "media", file.Hash, file.InputHash, (int)(file.LastUnix / 86_400)));
        }

        if (!string.IsNullOrWhiteSpace(settings.TranscriptionModel))
        {
            queued += media.Needing(AiJobKind.Transcribe, settings.TranscriptionModel.Trim()).Count(file =>
                _jobs.Enqueue(AiJobKind.Transcribe, "media", file.Hash, file.InputHash, (int)(file.LastUnix / 86_400)));
        }

        if (queued > 0)
        {
            _runner.Poke();
        }

        return queued;
    }

    /// <summary>
    /// Queues embedding for every thread with sessions the configured model has not embedded (A6).
    /// </summary>
    /// <remarks>
    /// Changing the embedding model is one of the invalidations §11.1 lists: every thread is then
    /// out of date for the new model and is queued, while search carries on with the old vectors.
    /// A blank model queues nothing — search stays keyword-only, which is its normal state.
    /// </remarks>
    public int PlanEmbeddings()
    {
        if (embeddings is null || state is null || string.IsNullOrWhiteSpace(state.Current.EmbeddingModel))
        {
            return 0;
        }

        var model = state.Current.EmbeddingModel.Trim();

        var queued = embeddings.ThreadsNeeding(model).Count(thread =>
            _jobs.Enqueue(AiJobKind.Embed, "thread", thread.ThreadId, thread.InputHash, (int)(thread.LastUnix / 86_400)));

        if (queued > 0)
        {
            _runner.Poke();
        }

        return queued;
    }

    /// <summary>
    /// Queues the diary texts that are out of date and settled enough to write (A5).
    /// </summary>
    /// <remarks>
    /// Months first, then what summarises them: the planner looks at the queue to decide whether a
    /// year is ready, and a month queued a moment ago has to be there for it to see.
    /// </remarks>
    public int PlanDiary()
    {
        if (diary is null)
        {
            return 0;
        }

        var queued = diary.Months().Count(Enqueue);

        queued += diary.Summaries().Count(Enqueue);

        if (queued > 0)
        {
            _runner.Poke();
        }

        return queued;

        bool Enqueue(DiaryWork work) =>
            _jobs.Enqueue(work.Kind, "person", work.SubjectId, work.InputHash, work.Priority);
    }

    /// <summary>How many sessions extraction would read now — the number the confirmation shows.</summary>
    public int PendingExtraction() =>
        Unread(PromptCatalog.VersionOf(PromptCatalog.ExtractSession)).Count;

    /// <summary>
    /// Sessions worth reading that nothing has read at this prompt version.
    /// </summary>
    /// <remarks>
    /// Excluded people and threads never appear, so a conversation the user has asked to be left
    /// alone is not merely skipped when its turn comes — it is never queued, and with a hosted
    /// endpoint that is the difference between "not analysed" and "not sent".
    /// </remarks>
    private List<(string SessionId, string MemberHash, long EndedAt)> Unread(string promptVersion)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT s.id, s.member_hash, s.ended_at_unix
            FROM session AS s
            JOIN thread AS t ON t.id = s.thread_id
            WHERE s.is_substantive = 1
              AND s.segmenter_version = $segmenter
              AND t.ai_excluded = 0
              AND NOT EXISTS (SELECT 1 FROM save_meta WHERE ai_opt_out = 1)
              AND NOT (t.kind = 'dm' AND EXISTS (
                  SELECT 1
                  FROM thread_participant AS tp
                  JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
                  JOIN person AS p ON p.id = ip.person_id
                  WHERE tp.thread_id = t.id AND p.ai_excluded = 1))
              AND NOT EXISTS (
                  SELECT 1 FROM derived_artifact AS d
                  WHERE d.source_session_id = s.id
                    AND d.kind = 'session_extract'
                    AND d.prompt_version = $prompt
              )
            ORDER BY s.ended_at_unix DESC;
            """;

        command.Parameters.AddWithValue("$segmenter", SessionSegmenter.Version);
        command.Parameters.AddWithValue("$prompt", promptVersion);

        var sessions = new List<(string, string, long)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return sessions;
    }
}
