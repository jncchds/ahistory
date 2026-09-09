-- 001_core.sql — the archive proper.
--
-- Conventions: STRICT tables, TEXT ids, ISO "O" UTC timestamps, ON DELETE CASCADE.
-- Section references (§n) point at message-archive-design.md.

-- §9: a save built from someone else's archive is a fundamentally different object, so the
-- distinction is stored, not just remembered. Single row, id pinned to 1.
CREATE TABLE save_meta (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    owner_is_self INTEGER NOT NULL DEFAULT 1 CHECK (owner_is_self IN (0, 1)),
    provenance    TEXT,
    created_utc   TEXT NOT NULL
) STRICT;

-- Where data came from, as distinct from the act of importing it.
--
-- Re-exporting an account six months later does not produce a new source — it produces a newer
-- version of the same one. Messages belong to a source; the UI filters on a source; §9's
-- provenance is a property of a source. Runs of an import are recorded separately, below.
--
-- The id is derived from the account the export belongs to (telegram:account:777001), so two
-- exports of the same account land on one source without anyone having to say so.
CREATE TABLE import_source (
    id          TEXT PRIMARY KEY,
    platform    TEXT NOT NULL,
    -- Editable: this is what the filter shows, and "telegram:account:777001" is not a name.
    label       TEXT,
    -- §9: where this came from and who gave it to you. Required for third-party archives.
    provenance  TEXT,
    created_utc TEXT NOT NULL
) STRICT;

CREATE INDEX ix_import_source_platform ON import_source (platform);

-- §1: every message traces back to a row here. One row per *run* — per act of importing —
-- so a source's history is auditable even though its messages are not re-linked each time.
CREATE TABLE import (
    id                 TEXT PRIMARY KEY,
    source_id          TEXT NOT NULL REFERENCES import_source (id) ON DELETE CASCADE,
    platform           TEXT NOT NULL,
    source_path        TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    importer_version   TEXT NOT NULL,
    status             TEXT NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
    started_utc        TEXT NOT NULL,
    finished_utc       TEXT,
    stats_json         TEXT,
    last_error         TEXT
) STRICT;

-- §1: the merged human.
CREATE TABLE person (
    id           TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    is_owner     INTEGER NOT NULL DEFAULT 0 CHECK (is_owner IN (0, 1)),
    notes        TEXT,
    created_utc  TEXT NOT NULL
) STRICT;

-- §1 warns that merging a contact into the owner poisons the whole knowledge base. A partial
-- unique index makes a second owner unrepresentable rather than merely discouraged by the UI.
CREATE UNIQUE INDEX ux_person_owner ON person (is_owner) WHERE is_owner = 1;

-- §1: one row per platform account. Messages reference identities, never persons.
CREATE TABLE identity (
    id                 TEXT PRIMARY KEY,
    platform           TEXT NOT NULL,
    source_identity_id TEXT,
    handle             TEXT,
    display_name       TEXT NOT NULL,
    -- §2: old and channel messages carry a name with no id. Synthetic identities are merge
    -- candidates, never dedupe anchors.
    is_synthetic       INTEGER NOT NULL DEFAULT 0 CHECK (is_synthetic IN (0, 1)),
    first_import_id    TEXT NOT NULL REFERENCES import (id),
    created_utc        TEXT NOT NULL
) STRICT;

CREATE UNIQUE INDEX ux_identity_source ON identity (platform, source_identity_id)
    WHERE source_identity_id IS NOT NULL;
CREATE UNIQUE INDEX ux_identity_name ON identity (platform, display_name)
    WHERE source_identity_id IS NULL;

-- §1: merging contacts = repointing identities at a person. The primary key on identity_id
-- means one person per identity, so a merge is one UPDATE and an unmerge is one UPDATE back.
-- Message rows are never rewritten.
CREATE TABLE identity_person (
    identity_id TEXT PRIMARY KEY REFERENCES identity (id) ON DELETE CASCADE,
    person_id   TEXT NOT NULL REFERENCES person (id) ON DELETE CASCADE,
    confidence  TEXT NOT NULL CHECK (confidence IN ('seed', 'auto', 'manual')),
    linked_utc  TEXT NOT NULL
) STRICT;

CREATE INDEX ix_identity_person ON identity_person (person_id);

CREATE TABLE thread (
    id               TEXT PRIMARY KEY,
    platform         TEXT NOT NULL,
    source_thread_id TEXT NOT NULL,
    kind             TEXT NOT NULL CHECK (kind IN ('dm', 'group', 'channel', 'saved')),
    title            TEXT,
    first_import_id  TEXT NOT NULL REFERENCES import (id),
    created_utc      TEXT NOT NULL
) STRICT;

CREATE UNIQUE INDEX ux_thread_source ON thread (platform, source_thread_id);

CREATE TABLE thread_participant (
    thread_id       TEXT NOT NULL REFERENCES thread (id) ON DELETE CASCADE,
    identity_id     TEXT NOT NULL REFERENCES identity (id) ON DELETE CASCADE,
    first_seen_unix INTEGER NOT NULL,
    PRIMARY KEY (thread_id, identity_id)
) STRICT;

-- §1: content-addressed. Exports repeat the same stickers and forwards hundreds of times.
CREATE TABLE media (
    hash             TEXT PRIMARY KEY,
    byte_size        INTEGER NOT NULL,
    mime             TEXT,
    -- Part of the on-disk filename, not decoration. V1 opens audio and video in the OS default
    -- handler (decisions.md D7), and every desktop platform picks that handler by extension —
    -- an extensionless file simply fails to open.
    extension        TEXT,
    media_kind       TEXT NOT NULL CHECK (media_kind IN
        ('photo', 'video', 'voice', 'video_message', 'audio', 'sticker', 'animation', 'file', 'thumbnail')),
    width            INTEGER,
    height           INTEGER,
    duration_seconds INTEGER,
    first_import_id  TEXT NOT NULL REFERENCES import (id),
    created_utc      TEXT NOT NULL
) STRICT;

-- §6.1: a session is a run of messages bounded by a gap of silence, and is the smallest
-- semantically self-contained unit. Sessions belong to a THREAD; the per-person view is a
-- union across threads and composes sessions rather than defining them.
--
-- Created in V1 because segmentation is pure time-gap logic with no LLM involved, and because
-- writing message.session_id later is then one UPDATE instead of altering a 500k-row table.
CREATE TABLE session (
    id                TEXT PRIMARY KEY,
    thread_id         TEXT NOT NULL REFERENCES thread (id) ON DELETE CASCADE,
    started_at_unix   INTEGER NOT NULL,
    ended_at_unix     INTEGER NOT NULL,
    message_count     INTEGER NOT NULL,
    -- §6.4: cache key for everything derived from this session.
    member_hash       TEXT NOT NULL,
    segmenter_version TEXT NOT NULL
) STRICT;

CREATE INDEX ix_session_thread_time ON session (thread_id, started_at_unix);

-- The central table.
--
-- id is an INTEGER PRIMARY KEY rather than TEXT (decisions.md D4): FTS5 external-content
-- tables address the content table by content_rowid, and SQLite may renumber rowids during
-- VACUUM on a table without an explicit integer primary key. Stable identity lives in uid,
-- which is the dedupe key and the citation target for the knowledge-base work.
--
-- Timestamps are stored twice (D5): sent_at_utc is authoritative, sent_at_unix is the
-- ordering and keyset-pagination key that every hot index is built on.
CREATE TABLE message (
    id                 INTEGER PRIMARY KEY,
    uid                TEXT    NOT NULL,
    thread_id          TEXT    NOT NULL REFERENCES thread (id) ON DELETE CASCADE,
    sender_identity_id TEXT    REFERENCES identity (id) ON DELETE RESTRICT,
    kind               TEXT    NOT NULL CHECK (kind IN ('message', 'service')),
    -- §2: service messages carry actor/actor_id and an action instead of from/from_id.
    service_action     TEXT,
    sent_at_utc        TEXT    NOT NULL,
    sent_at_unix       INTEGER NOT NULL,
    tz_offset_minutes  INTEGER,
    -- §2: built by concatenating text_entities, never from the polymorphic text field.
    plaintext          TEXT    NOT NULL DEFAULT '',
    entities_json      TEXT,
    -- §1: fallback dedupe key, and how an edited body is detected on re-import.
    content_hash       TEXT    NOT NULL,
    -- A uid, not a foreign key: export order is not topological and a reply can precede its
    -- target across files. Resolution is a read-time join.
    reply_to_uid       TEXT,
    forwarded_from     TEXT,
    forwarded_at_utc   TEXT,
    via_bot            TEXT,
    edited_at_utc      TEXT,
    is_deleted         INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
    session_id         TEXT    REFERENCES session (id) ON DELETE SET NULL,
    -- §1: keep the raw export per message, so a parser gap is re-run rather than re-requested.
    raw_json           TEXT,
    first_import_id    TEXT    NOT NULL REFERENCES import (id),
    importer_version   TEXT    NOT NULL
) STRICT;

CREATE UNIQUE INDEX ux_message_uid   ON message (uid);
CREATE INDEX ix_message_thread_time  ON message (thread_id, sent_at_unix, id);
CREATE INDEX ix_message_sender_time  ON message (sender_identity_id, sent_at_unix, id);
CREATE INDEX ix_message_content_hash ON message (content_hash);
CREATE INDEX ix_message_reply        ON message (reply_to_uid) WHERE reply_to_uid IS NOT NULL;
CREATE INDEX ix_message_session      ON message (session_id) WHERE session_id IS NOT NULL;

-- Which sources a message belongs to — the table the UI's "show only these" filter reads.
--
-- Deliberately keyed by SOURCE, not by run. Re-importing a newer export of an account you
-- already have would otherwise write one row per message every time, so a 500k-message archive
-- would pay 500k writes to learn that almost nothing changed. Keyed by source, a re-run inserts
-- rows only for messages that are genuinely new.
--
-- A message can still belong to several sources — your own export and an archive someone gave
-- you may both contain the same group conversation — which is exactly what the filter is for.
--
-- first_import_id keeps the run-level answer available: "what did this particular run add?" is
-- one indexed read, and it is what makes a bad run reversible.
CREATE TABLE message_source (
    message_id      INTEGER NOT NULL REFERENCES message (id) ON DELETE CASCADE,
    source_id       TEXT    NOT NULL REFERENCES import_source (id) ON DELETE CASCADE,
    first_import_id TEXT    NOT NULL REFERENCES import (id),
    seen_utc        TEXT    NOT NULL,
    PRIMARY KEY (message_id, source_id)
) STRICT;

CREATE INDEX ix_message_source_source ON message_source (source_id, message_id);
CREATE INDEX ix_message_source_run    ON message_source (first_import_id);

-- Not in the spec, but present in exports: a later export of the same chat can carry different
-- text for the same message id. §1 makes messages immutable after import, so a changed body
-- becomes a revision row and the original is never lost.
CREATE TABLE message_revision (
    id                 INTEGER PRIMARY KEY,
    message_id         INTEGER NOT NULL REFERENCES message (id) ON DELETE CASCADE,
    observed_import_id TEXT    NOT NULL REFERENCES import (id),
    plaintext          TEXT    NOT NULL,
    entities_json      TEXT,
    content_hash       TEXT    NOT NULL,
    raw_json           TEXT,
    observed_utc       TEXT    NOT NULL
) STRICT;

CREATE INDEX ix_revision_message ON message_revision (message_id);

-- Also absent from the spec, also present in exports, and strong behavioural signal for §7.
-- The unique index is what makes re-importing reactions a no-op instead of a duplicate storm.
CREATE TABLE reaction (
    id                INTEGER PRIMARY KEY,
    message_id        INTEGER NOT NULL REFERENCES message (id) ON DELETE CASCADE,
    emoji             TEXT    NOT NULL,
    custom_emoji_id   TEXT,
    actor_identity_id TEXT    REFERENCES identity (id) ON DELETE RESTRICT,
    count             INTEGER NOT NULL DEFAULT 1,
    reacted_at_utc    TEXT
) STRICT;

CREATE UNIQUE INDEX ux_reaction ON reaction (message_id, emoji, ifnull(actor_identity_id, ''));

CREATE TABLE message_media (
    message_id        INTEGER NOT NULL REFERENCES message (id) ON DELETE CASCADE,
    ordinal           INTEGER NOT NULL,
    -- NULL when the export omitted the file ("File not included. Change data exporting
    -- settings..."). That is a normal state, not an import failure.
    media_hash        TEXT    REFERENCES media (hash) ON DELETE RESTRICT,
    export_path       TEXT    NOT NULL,
    original_filename TEXT,
    missing_reason    TEXT,
    sticker_emoji     TEXT,
    PRIMARY KEY (message_id, ordinal)
) STRICT;

CREATE INDEX ix_message_media_hash ON message_media (media_hash);
