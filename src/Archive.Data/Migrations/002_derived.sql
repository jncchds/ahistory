-- 002_derived.sql — seams for the knowledge-base and diary work (§6, §7, §8).
--
-- Nothing in V1 writes to these tables. They exist now because each is cheap to create and
-- expensive or impossible to retrofit: adding an edge subject or a second time axis to a fact
-- store that already holds facts means migrating every row and every query that reads them.

-- §7: relational facts live on the edge, not the node. "Met in Berlin in 2015" and "stopped
-- speaking for two years" belong to the pair and render in both diaries from one row.
-- Extracting each side separately produces two contradictory versions of the same history.
--
-- The CHECK enforces canonical ordering so an unordered pair cannot exist twice.
CREATE TABLE person_edge (
    id          TEXT PRIMARY KEY,
    person_a_id TEXT NOT NULL REFERENCES person (id) ON DELETE CASCADE,
    person_b_id TEXT NOT NULL REFERENCES person (id) ON DELETE CASCADE,
    created_utc TEXT NOT NULL,
    CHECK (person_a_id < person_b_id)
) STRICT;

CREATE UNIQUE INDEX ux_person_edge ON person_edge (person_a_id, person_b_id);

-- §3 and §6.6: every machine-produced text is an artifact hanging off its source, never a
-- replacement for it. One table for transcripts, OCR, captions, embeddings, session extracts,
-- rollups and diary entries, so that "re-run only what was produced with prompt < v4" is one
-- query rather than a full rebuild.
--
-- Re-running a model INSERTS a new row and never overwrites, so diffing an upgraded transcript
-- against the old one is also just a query (§3).
CREATE TABLE derived_artifact (
    id                TEXT PRIMARY KEY,
    kind              TEXT NOT NULL CHECK (kind IN
        ('transcript', 'ocr', 'caption', 'embedding', 'session_extract', 'rollup', 'diary')),
    source_media_hash TEXT    REFERENCES media (hash) ON DELETE CASCADE,
    source_message_id INTEGER REFERENCES message (id) ON DELETE CASCADE,
    source_session_id TEXT    REFERENCES session (id) ON DELETE CASCADE,
    engine            TEXT NOT NULL,
    model             TEXT NOT NULL,
    model_version     TEXT NOT NULL,
    prompt_version    TEXT,
    language          TEXT,
    -- §3: a confident transcript, a shaky transcript and OCR are three levels of trust.
    confidence        REAL,
    payload_json      TEXT NOT NULL,
    -- §6.4: hash of this artifact's inputs. Re-importing an old export dirties a few nodes
    -- rather than the archive.
    input_hash        TEXT NOT NULL,
    created_utc       TEXT NOT NULL
) STRICT;

CREATE INDEX ix_artifact_source ON derived_artifact (source_media_hash, kind, model_version);
CREATE INDEX ix_artifact_rerun  ON derived_artifact (kind, prompt_version);

-- §7: facts are append-only. A correction is a NEW row; the old one is closed out by pointing
-- at its replacement. Nothing is ever updated in place, which is what keeps "why does it think
-- this?" answerable and matches the archive's own rule that message rows are never rewritten.
--
-- Two independent time axes:
--   valid_from_utc / valid_to_utc  — event time: when the claim was true of the world.
--   asserted_utc / retracted_utc   — assertion time: when we came to believe it.
-- Both are needed so a diary entry for 2019 is not narrated with knowledge that arrived in
-- 2023. "Works at Acme" from 2019 is not wrong, it is expired (§7).
CREATE TABLE fact (
    id                  TEXT PRIMARY KEY,
    -- A fact is about a person or about a pair, never both, never neither.
    subject_person_id   TEXT REFERENCES person (id) ON DELETE CASCADE,
    subject_edge_id     TEXT REFERENCES person_edge (id) ON DELETE CASCADE,
    -- Normalized (predicate, object) alongside the readable claim: the normalized form is the
    -- free auto-merge fast path, the readable form is what a diary sentence is built from.
    predicate           TEXT NOT NULL,
    object_text         TEXT NOT NULL,
    claim_text          TEXT NOT NULL,
    -- §7: self-report, reflected (someone else says it to them), or behavioral (derived from
    -- metadata, no LLM). Reflected evidence captures what people never say about themselves
    -- and is also the easiest to get wrong — jokes and sarcasm read as sincere claims.
    evidence_kind       TEXT NOT NULL CHECK (evidence_kind IN ('self_report', 'reflected', 'behavioral')),
    -- §4: said in a DM or to eleven people. The same sentence is much weaker evidence in a group.
    origin_kind         TEXT NOT NULL CHECK (origin_kind IN ('dm', 'group')),
    confidence          REAL NOT NULL,
    valid_from_utc      TEXT,
    valid_to_utc        TEXT,
    asserted_utc        TEXT NOT NULL,
    retracted_utc       TEXT,
    superseded_by       TEXT REFERENCES fact (id),
    derived_artifact_id TEXT NOT NULL REFERENCES derived_artifact (id) ON DELETE CASCADE,
    CHECK ((subject_person_id IS NULL) <> (subject_edge_id IS NULL))
) STRICT;

CREATE INDEX ix_fact_person ON fact (subject_person_id) WHERE subject_person_id IS NOT NULL;
CREATE INDEX ix_fact_edge   ON fact (subject_edge_id) WHERE subject_edge_id IS NOT NULL;
-- "What does it currently believe about this person?" must not scan the whole history.
CREATE INDEX ix_fact_live   ON fact (subject_person_id, predicate) WHERE superseded_by IS NULL;

-- Every message that influenced the decision, and in what way.
--
-- The role is the point. A flat citation list tells you which messages are related to a fact;
-- roles tell you which one first made the claim, which corroborated it, which contradicted it,
-- and specifically which message set each validity date. Without that, a wrong date can be
-- seen but never audited back to its source.
--
-- The validity dates are found by querying for their role rather than by a pointer column on
-- `fact`: a pointer would make fact and fact_citation reference each other in a cycle, which
-- cannot be satisfied by inserts alone, and facts are append-only.
CREATE TABLE fact_citation (
    id         INTEGER PRIMARY KEY,
    fact_id    TEXT    NOT NULL REFERENCES fact (id) ON DELETE CASCADE,
    message_id INTEGER NOT NULL REFERENCES message (id) ON DELETE CASCADE,
    role       TEXT    NOT NULL CHECK (role IN
        ('asserts', 'corroborates', 'contradicts', 'establishes_valid_from', 'establishes_valid_to')),
    -- Where in the message the claim was found, when the extractor can say.
    quote      TEXT
) STRICT;

CREATE UNIQUE INDEX ux_fact_citation ON fact_citation (fact_id, message_id, role);
CREATE INDEX ix_fact_citation_message ON fact_citation (message_id);
