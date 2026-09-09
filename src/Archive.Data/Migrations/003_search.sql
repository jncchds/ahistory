-- 003_search.sql — the search surface (§5, and §3's provenance rule).
--
-- Search does not index `message` directly. It indexes `search_document`, which carries a
-- provenance column, because §3 requires a confident transcript, a shaky transcript and OCR to
-- be distinguishable in results: "Whisper mishearing a name will otherwise become biography."
--
-- V1 only ever writes provenance='message' rows. The indirection is what lets transcription
-- and OCR join the same searchable surface later by INSERTING rows — no re-index of messages,
-- no rewrite of the search queries, and no new result component.
CREATE TABLE search_document (
    id                  INTEGER PRIMARY KEY,
    message_id          INTEGER REFERENCES message (id) ON DELETE CASCADE,
    derived_artifact_id TEXT    REFERENCES derived_artifact (id) ON DELETE CASCADE,
    provenance          TEXT    NOT NULL CHECK (provenance IN ('message', 'transcript', 'ocr', 'caption')),
    -- Denormalized so a filtered search is a test on the matched row rather than a join.
    thread_id           TEXT    NOT NULL,
    sender_identity_id  TEXT,
    sent_at_unix        INTEGER NOT NULL,
    body                TEXT    NOT NULL,
    -- A document describes exactly one source.
    CHECK ((message_id IS NULL) <> (derived_artifact_id IS NULL))
) STRICT;

CREATE UNIQUE INDEX ux_search_message ON search_document (message_id)
    WHERE message_id IS NOT NULL;
CREATE UNIQUE INDEX ux_search_artifact ON search_document (derived_artifact_id)
    WHERE derived_artifact_id IS NOT NULL;

-- Tokenizer choice (decisions.md D8): the archive is multilingual and includes Ukrainian and
-- Russian. There is no FTS5 stemmer for Slavic languages, so exact tokens will not match across
-- inflection. Slavic inflection is overwhelmingly suffixal, so a prefix index plus a query
-- rewriter that appends * to bare terms recovers most of it at a fraction of trigram's cost.
--
-- remove_diacritics 2 is the Unicode-aware variant; the older mode 1 mishandles some
-- multi-codepoint sequences.
CREATE VIRTUAL TABLE search_fts USING fts5(
    body,
    provenance         UNINDEXED,
    thread_id          UNINDEXED,
    sender_identity_id UNINDEXED,
    sent_at_unix       UNINDEXED,
    content='search_document',
    content_rowid='id',
    tokenize="unicode61 remove_diacritics 2",
    prefix='2 3'
);

-- message -> search_document.
--
-- Only body-affecting changes matter, so the update trigger is scoped to plaintext. An edit
-- arriving on re-import updates the message body (the original is preserved as a
-- message_revision row), and the index follows.
CREATE TRIGGER trg_message_ai AFTER INSERT ON message BEGIN
    INSERT INTO search_document (message_id, provenance, thread_id, sender_identity_id, sent_at_unix, body)
    VALUES (new.id, 'message', new.thread_id, new.sender_identity_id, new.sent_at_unix, new.plaintext);
END;

CREATE TRIGGER trg_message_au AFTER UPDATE OF plaintext ON message BEGIN
    UPDATE search_document SET body = new.plaintext WHERE message_id = new.id;
END;

-- search_document -> search_fts.
--
-- An external-content FTS5 table does not track its content table by itself. Deletes must be
-- announced with the 'delete' command AND must repeat the old column values, or the index
-- keeps stale postings that still match queries.
--
-- Note that these triggers only fire for cascaded deletes when PRAGMA recursive_triggers is
-- ON. Database.cs sets it on every connection for exactly this reason; without it, deleting a
-- thread would leave orphaned rows in search_fts.
CREATE TRIGGER trg_search_document_ai AFTER INSERT ON search_document BEGIN
    INSERT INTO search_fts (rowid, body, provenance, thread_id, sender_identity_id, sent_at_unix)
    VALUES (new.id, new.body, new.provenance, new.thread_id, new.sender_identity_id, new.sent_at_unix);
END;

CREATE TRIGGER trg_search_document_ad AFTER DELETE ON search_document BEGIN
    INSERT INTO search_fts (search_fts, rowid, body, provenance, thread_id, sender_identity_id, sent_at_unix)
    VALUES ('delete', old.id, old.body, old.provenance, old.thread_id, old.sender_identity_id, old.sent_at_unix);
END;

CREATE TRIGGER trg_search_document_au AFTER UPDATE ON search_document BEGIN
    INSERT INTO search_fts (search_fts, rowid, body, provenance, thread_id, sender_identity_id, sent_at_unix)
    VALUES ('delete', old.id, old.body, old.provenance, old.thread_id, old.sender_identity_id, old.sent_at_unix);
    INSERT INTO search_fts (rowid, body, provenance, thread_id, sender_identity_id, sent_at_unix)
    VALUES (new.id, new.body, new.provenance, new.thread_id, new.sender_identity_id, new.sent_at_unix);
END;
