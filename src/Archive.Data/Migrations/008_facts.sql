-- 008_facts.sql — what extraction needs from a fact store that was built empty in V1.
--
-- 002_derived.sql laid out `fact`, `fact_citation` and `derived_artifact` before anything wrote to
-- them. Three things it could not express turn out to matter now: who asserted a fact, what a
-- rollup or a diary entry hangs off, and which correspondence is not to be read at all.

-- Who says so.
--
-- The failure this prevents is the one that would make the facts panel not worth correcting: you
-- read a fact, see it is wrong, fix it — and the next prompt version retracts your correction
-- along with everything else that run produced. A user-sourced fact is never touched by a re-run,
-- and a user-deleted one is a tombstone rather than a missing row, so a re-run that would assert
-- the same thing again records the citation and stays deleted (ai-plan.md §5.6).
ALTER TABLE fact ADD COLUMN source TEXT NOT NULL DEFAULT 'extracted'
    CHECK (source IN ('extracted', 'user_edited', 'user_deleted'));

-- §6.4 and §8: a session extract hangs off a session, but a rollup and a diary entry hang off a
-- PERSON AND A WINDOW — the one shape the table was not given. Added as nullable columns, which is
-- all SQLite permits for a foreign key added after the fact, and all that is wanted: an artifact
-- has exactly one kind of source and the rest stay null.
ALTER TABLE derived_artifact ADD COLUMN source_person_id  TEXT REFERENCES person (id) ON DELETE CASCADE;
ALTER TABLE derived_artifact ADD COLUMN source_edge_id    TEXT REFERENCES person_edge (id) ON DELETE CASCADE;
ALTER TABLE derived_artifact ADD COLUMN window_start_unix INTEGER;
ALTER TABLE derived_artifact ADD COLUMN window_end_unix   INTEGER;

-- "The diary entry for this person and month" must not scan every artifact in the archive.
CREATE INDEX ix_artifact_window ON derived_artifact (source_person_id, kind, window_start_unix)
    WHERE source_person_id IS NOT NULL;

CREATE INDEX ix_artifact_session ON derived_artifact (source_session_id, kind, prompt_version)
    WHERE source_session_id IS NOT NULL;

-- Correspondence that is not to be profiled at all.
--
-- With a hosted endpoint this is also the difference between "not analysed" and "not sent", which
-- is the stronger promise and the one worth being able to make. Cheap now; awkward to retrofit
-- once there are jobs in flight that were planned without it.
ALTER TABLE person ADD COLUMN ai_excluded INTEGER NOT NULL DEFAULT 0 CHECK (ai_excluded IN (0, 1));
ALTER TABLE thread ADD COLUMN ai_excluded INTEGER NOT NULL DEFAULT 0 CHECK (ai_excluded IN (0, 1));

-- §9: a save built from someone else's archive should not start being profiled because the
-- machine it was opened on has AI switched on. Enabling is a machine setting; this is the save's
-- own answer, and it wins.
ALTER TABLE save_meta ADD COLUMN ai_opt_out INTEGER NOT NULL DEFAULT 0 CHECK (ai_opt_out IN (0, 1));
