-- 007_ai_jobs.sql — the work queue, and the two things segmentation learns about a session.
--
-- Everything the AI layer does is a row in ai_job drained by a background runner: there is no
-- separate "index everything" mode, because that would be the same code with a different button
-- on it (ai-plan.md §11.1). Work is enqueued by invalidation — enabling AI, finishing an import,
-- bumping a prompt version — and the only controls are start, pause and how fast.

CREATE TABLE ai_job (
    id              INTEGER PRIMARY KEY,
    kind            TEXT    NOT NULL CHECK (kind IN
        ('segment', 'extract', 'adjudicate', 'rollup', 'diary', 'embed')),
    subject_kind    TEXT    NOT NULL CHECK (subject_kind IN ('thread', 'session', 'person', 'edge')),
    subject_id      TEXT    NOT NULL,
    state           TEXT    NOT NULL CHECK (state IN
        ('pending', 'running', 'done', 'failed', 'needs_review')),
    -- Higher first. A ten-year archive drained oldest-first shows nothing useful for hours, which
    -- reads as broken; recency and how much a person is written to decide what happens first.
    priority        INTEGER NOT NULL DEFAULT 0,
    attempts        INTEGER NOT NULL DEFAULT 0,
    -- When the claim on a running job expires. A crash is otherwise indistinguishable from a job
    -- still working, and every job left mid-flight would stay 'running' forever.
    lease_utc       TEXT,
    -- §6.6: which prompt and model produced this. Part of the identity of the work, so that
    -- "re-run only what was done with prompt < v4" is a WHERE clause and not a rebuild. NULL for
    -- work no model is involved in, which is all of A2.
    prompt_version  TEXT,
    model_version   TEXT,
    -- §6.4: hash of the inputs. Re-running with the same inputs and the same versions is a no-op.
    input_hash      TEXT,
    -- The kind of failure, never its text (P6): 'timeout', 'http_429', 'bad_response'.
    last_error_kind TEXT,
    created_utc     TEXT    NOT NULL,
    updated_utc     TEXT    NOT NULL
) STRICT;

-- One job per piece of work at a given version. A new prompt version is new work and gets its own
-- row; asking for the same work twice does not.
CREATE UNIQUE INDEX ux_ai_job ON ai_job (
    kind, subject_id, coalesce(prompt_version, ''), coalesce(model_version, ''));

CREATE INDEX ix_ai_job_queue ON ai_job (state, priority DESC, id);
CREATE INDEX ix_ai_job_lease ON ai_job (state, lease_utc) WHERE state = 'running';

-- §6.2: a large fraction of sessions are pure logistics — "on my way", "ok", a sticker. Deciding
-- that with cheap heuristics rather than a model is what keeps the token bill to the 20-30% of
-- sessions that carry anything.
--
-- On `session` rather than in its own table because it is one verdict per session, recomputed in
-- place: the classifier is deterministic and free, so there is nothing to keep a history of. The
-- version is what makes "re-filter everything decided by the old rules" answerable.
ALTER TABLE session ADD COLUMN is_substantive INTEGER CHECK (is_substantive IN (0, 1));
ALTER TABLE session ADD COLUMN filter_version TEXT;

-- The two counts the coverage line is built from: how much there is, and how much is worth
-- reading. Partial, because most of a large archive is neither.
CREATE INDEX ix_session_substantive ON session (is_substantive) WHERE is_substantive = 1;
