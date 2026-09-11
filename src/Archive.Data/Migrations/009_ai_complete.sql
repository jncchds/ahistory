-- 009_ai_complete.sql — what merging, the diary, semantic search and reading media need.
--
-- One migration for four phases rather than one each, because the four were built together:
-- ai-plan.md §13's rule against a migration per layer is about not pinning a guess at a schema for
-- work that does not exist yet, and none of this is a guess any more.

-- The queue learns two kinds of work that hang off a file rather than a conversation.
--
-- SQLite cannot alter a CHECK constraint, so the table is rebuilt: created under a new name,
-- filled, and swapped in. Migrations run with foreign keys off and nothing references ai_job, so
-- the swap is safe; every row keeps its id, state and attempts.
CREATE TABLE ai_job_new (
    id              INTEGER PRIMARY KEY,
    kind            TEXT    NOT NULL CHECK (kind IN
        ('segment', 'extract', 'adjudicate', 'rollup', 'diary', 'embed', 'transcribe', 'ocr')),
    subject_kind    TEXT    NOT NULL CHECK (subject_kind IN ('thread', 'session', 'person', 'edge', 'media')),
    subject_id      TEXT    NOT NULL,
    state           TEXT    NOT NULL CHECK (state IN
        ('pending', 'running', 'done', 'failed', 'needs_review')),
    priority        INTEGER NOT NULL DEFAULT 0,
    attempts        INTEGER NOT NULL DEFAULT 0,
    lease_utc       TEXT,
    prompt_version  TEXT,
    model_version   TEXT,
    input_hash      TEXT,
    last_error_kind TEXT,
    created_utc     TEXT    NOT NULL,
    updated_utc     TEXT    NOT NULL
) STRICT;

INSERT INTO ai_job_new SELECT * FROM ai_job;
DROP TABLE ai_job;
ALTER TABLE ai_job_new RENAME TO ai_job;

CREATE UNIQUE INDEX ux_ai_job ON ai_job (
    kind, subject_id, coalesce(prompt_version, ''), coalesce(model_version, ''));

CREATE INDEX ix_ai_job_queue ON ai_job (state, priority DESC, id);
CREATE INDEX ix_ai_job_lease ON ai_job (state, lease_utc) WHERE state = 'running';

-- And the statistics learn the same two purposes, and that a call can be about a thread (an
-- embedding batch) or a file. Rebuilt for the same reason.
CREATE TABLE ai_interaction_new (
    id                INTEGER PRIMARY KEY,
    created_utc       TEXT    NOT NULL,
    purpose           TEXT    NOT NULL CHECK (purpose IN
        ('extract', 'adjudicate', 'rollup', 'diary', 'embed', 'transcribe', 'ocr', 'list_models', 'test')),
    provider          TEXT    NOT NULL,
    endpoint          TEXT    NOT NULL,
    model             TEXT    NOT NULL,
    subject_kind      TEXT    CHECK (subject_kind IN ('session', 'person', 'thread', 'media')),
    subject_id        TEXT,
    attempt           INTEGER NOT NULL,
    duration_ms       INTEGER NOT NULL,
    prompt_tokens     INTEGER,
    completion_tokens INTEGER,
    total_tokens      INTEGER,
    tool_call_count   INTEGER NOT NULL,
    finish_reason     TEXT,
    http_status       INTEGER,
    failed            INTEGER NOT NULL CHECK (failed IN (0, 1)),
    failure_kind      TEXT,
    request_json      TEXT,
    response_json     TEXT
) STRICT;

INSERT INTO ai_interaction_new SELECT * FROM ai_interaction;
DROP TABLE ai_interaction;
ALTER TABLE ai_interaction_new RENAME TO ai_interaction;

CREATE INDEX ix_ai_interaction_time    ON ai_interaction (created_utc);
CREATE INDEX ix_ai_interaction_purpose ON ai_interaction (purpose, created_utc);

-- §7: the owner's facts surface in a dozen conversations, phrased a dozen ways, and the diary is
-- an unreadable pile of restatements unless they are merged into one fact with many citations.
--
-- A merge is a pointer, not a rewrite. The duplicate keeps its own citations and its own history;
-- it simply stops being shown on its own. That makes a merge undoable by clearing one column —
-- which is what happens when the fact it was merged into is retracted by a re-run, so a merge can
-- never outlive the evidence it was folded into. Kept apart from superseded_by on purpose: that
-- one means "expired in event time", and a duplicate has not expired.
--
-- Deliberately not a declared foreign key. With one, removing a single fact another was merged
-- into would either fail or need a cascade that deletes the duplicate's own evidence; without one,
-- the merge step clears any pointer whose target has gone, and the duplicate simply reappears.
ALTER TABLE fact ADD COLUMN merged_into TEXT;

CREATE INDEX ix_fact_merged ON fact (merged_into) WHERE merged_into IS NOT NULL;

-- What a model concluded about two facts that looked alike: the same thing said twice, a value
-- that changed, or two things that are both true. Remembered so the question is asked once —
-- "different" in particular changes nothing else, and without this it would be asked again on
-- every pass. Canonically ordered, like an edge, so a pair cannot be judged twice two ways.
CREATE TABLE fact_pair_verdict (
    fact_a_id      TEXT NOT NULL REFERENCES fact (id) ON DELETE CASCADE,
    fact_b_id      TEXT NOT NULL REFERENCES fact (id) ON DELETE CASCADE,
    outcome        TEXT NOT NULL CHECK (outcome IN ('same', 'changed', 'different')),
    model          TEXT NOT NULL,
    prompt_version TEXT NOT NULL,
    created_utc    TEXT NOT NULL,
    PRIMARY KEY (fact_a_id, fact_b_id),
    CHECK (fact_a_id < fact_b_id)
) STRICT;

-- §9.1: the canonical store for vectors is a plain BLOB with its own dimension, one row per session
-- per model. Any model, any dimension, and two models side by side while one replaces the other —
-- which is what keeps semantic search working, in the old space, the whole time a new model is
-- being built. Little-endian float32, normalized to unit length when written, so similarity is a
-- dot product.
--
-- Cascades with the session: a session that stopped existing has nothing to be found by, and the
-- session that replaced it is embedded afresh.
CREATE TABLE embedding (
    session_id  TEXT    NOT NULL REFERENCES session (id) ON DELETE CASCADE,
    model       TEXT    NOT NULL,
    dim         INTEGER NOT NULL CHECK (dim > 0),
    vector      BLOB    NOT NULL,
    -- What was embedded: the session's membership and the text-building version. A session whose
    -- text would now be built differently is embedded again; one that has not changed is not.
    input_hash  TEXT    NOT NULL,
    created_utc TEXT    NOT NULL,
    PRIMARY KEY (session_id, model),
    CHECK (length(vector) = dim * 4)
) STRICT;

CREATE INDEX ix_embedding_model ON embedding (model);

-- "Everything read from this photo" and "the transcript of this voice message", newest first.
CREATE INDEX ix_artifact_media ON derived_artifact (source_media_hash, kind, created_utc)
    WHERE source_media_hash IS NOT NULL;
