-- 011_refill_lost_facts.sql — read again the conversations whose facts a merge deleted.
--
-- Until this release, merging two people deleted everything the model had learned about the one
-- that was merged away. The emptied person was removed, `fact` cascades from `person`, and the
-- facts went with them — but the session they were read from kept its `session_extract`, so it
-- counted as read at the current prompt and was never planned again. The same went for facts about
-- a pair that person was in, which cascade through `person_edge`. IdentityMerger now carries all of
-- it over to the person merged into; this is for the saves that merged before it did.
--
-- There is nothing left to move. The rows are gone, and the extract's payload records how many
-- facts a run wrote, not what they said. So the only way back is to read those conversations
-- again, and this puts their extraction back in the queue — which is all it does. Nothing is sent
-- anywhere by a migration: the job runs when the runner next drains, under the consent and the
-- exclusions in force then, and with AI switched off it simply waits.
--
-- Which sessions: those whose newest extract accounts for fewer rows than it reported writing.
-- Each fact a run wrote is one row, whatever has happened to it since — a user's deletion is a
-- tombstone on that same row, and a retraction or a merge only sets columns — except for a user's
-- edit, which adds a row of its own against the same extract and is therefore not counted. Older
-- extracts are left alone: their facts were already retracted by the run that replaced them, and
-- reading the conversation again restores what is believed now, not what was believed then.
--
-- A session whose run declined to write something the user had already ruled on also reports more
-- than it wrote, and is read once more for nothing. That costs one call; the user's ruling still
-- wins on the re-read, and telling the two apart is not possible from what is stored.
--
-- The job's input hash is extended rather than left as it was. The extract's id is derived from
-- the session, prompt, model and that hash, so a re-read under the old hash would land on the same
-- extract and collide with the ids of the facts that survived. Under a new hash it is an ordinary
-- re-run: a new extract, the survivors retracted in assertion time, the user's own rows untouched —
-- the path a prompt change already takes. The planner never sees the difference, because it only
-- asks about sessions with no extract at the current prompt, and these have one.
WITH latest AS (
    SELECT d.id, d.source_session_id AS session_id, d.payload_json
    FROM derived_artifact AS d
    WHERE d.kind = 'session_extract'
      AND d.source_session_id IS NOT NULL
      AND d.created_utc = (
          SELECT max(x.created_utc) FROM derived_artifact AS x
          WHERE x.kind = 'session_extract' AND x.source_session_id = d.source_session_id)
),
lost AS (
    SELECT latest.session_id
    FROM latest
    WHERE coalesce(json_extract(latest.payload_json, '$.facts'), 0) > (
        SELECT count(*) FROM fact AS f
        WHERE f.derived_artifact_id = latest.id AND f.source <> 'user_edited')
)
UPDATE ai_job
SET state       = 'pending',
    attempts    = 0,
    lease_utc   = NULL,
    input_hash  = coalesce(input_hash, '') || '|refill-011',
    updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE kind = 'extract'
  AND state = 'done'
  AND subject_id IN (SELECT session_id FROM lost);
