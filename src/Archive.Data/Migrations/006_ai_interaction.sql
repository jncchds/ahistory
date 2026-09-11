-- 006_ai_interaction.sql — the audit trail for every model call (ai-plan.md §4).
--
-- One row per call, success or failure. This is what answers "how much of the archive has been
-- read, what did it cost, and what is failing" without anyone having to watch a log scroll past.
--
-- It belongs in the save rather than beside the settings because it is about this archive's
-- processing, and because it is derived: deleting all of it costs nothing but the statistics.
CREATE TABLE ai_interaction (
    id                INTEGER PRIMARY KEY,
    created_utc       TEXT    NOT NULL,
    purpose           TEXT    NOT NULL CHECK (purpose IN
        ('extract', 'adjudicate', 'rollup', 'diary', 'embed', 'list_models', 'test')),
    provider          TEXT    NOT NULL,
    -- Scheme, host and path. Never the key, and never the query string, which is where some
    -- gateways put one.
    endpoint          TEXT    NOT NULL,
    model             TEXT    NOT NULL,
    -- What the call was about: a session, a person, or nothing for a settings-page call.
    subject_kind      TEXT    CHECK (subject_kind IN ('session', 'person')),
    subject_id        TEXT,
    -- Which try this was. A retried call is several rows, because "it succeeded" and "it
    -- succeeded on the third attempt against a provider that keeps returning 429" are different
    -- facts about the same archive.
    attempt           INTEGER NOT NULL,
    duration_ms       INTEGER NOT NULL,
    prompt_tokens     INTEGER,
    completion_tokens INTEGER,
    total_tokens      INTEGER,
    tool_call_count   INTEGER NOT NULL,
    finish_reason     TEXT,
    http_status       INTEGER,
    failed            INTEGER NOT NULL CHECK (failed IN (0, 1)),
    -- The kind of failure, not its text: 'timeout', 'unreachable', 'http_429', 'bad_response'.
    failure_kind      TEXT,
    -- Both NULL unless the user has switched on prompt recording. The request body is their
    -- correspondence, so this is off by default: on, it puts the same private text in the save a
    -- second time, in a form far easier to read out of by accident.
    request_json      TEXT,
    response_json     TEXT
) STRICT;

CREATE INDEX ix_ai_interaction_time    ON ai_interaction (created_utc);
CREATE INDEX ix_ai_interaction_purpose ON ai_interaction (purpose, created_utc);
