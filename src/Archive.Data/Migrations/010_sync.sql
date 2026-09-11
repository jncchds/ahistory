-- History fetched from a platform rather than read from an export (docs/live-sources-plan.md).
--
-- Three things an export never needed: which chats of a connected account the user wants in the
-- archive, how far each one has been read, and which stored messages the platform has since
-- deleted. None of them rewrites a message (P2); all three hang off rows that already exist.

-- The chats a connected account has, and what the user decided about each.
--
-- A connected account is every chat the person has ever been in, including channels they merely
-- follow. Pulling all of it into a personal archive buries the correspondence under broadcast
-- traffic, so nothing is read until it is included. A chat nobody has decided about yet is NULL,
-- which is not the same as ignored: it is listed for the user to decide, and live messages for it
-- are not stored, because the backfill that follows an "include" fetches them anyway.
--
-- chat_id is the platform's thread id — thread.source_thread_id — so a chat and the thread its
-- messages land in are joined without a lookup table. peer_kind is the platform's own
-- classification (Telegram: user, chat, channel), which the thread kind flattens and a deletion
-- needs: Telegram reports deletions in private chats and basic groups by message id alone.
CREATE TABLE sync_chat (
    source_id         TEXT NOT NULL REFERENCES import_source (id) ON DELETE CASCADE,
    chat_id           TEXT NOT NULL,
    peer_kind         TEXT NOT NULL,
    thread_kind       TEXT NOT NULL CHECK (thread_kind IN ('dm', 'group', 'channel', 'saved')),
    title             TEXT,
    decision          TEXT CHECK (decision IN ('include', 'ignore')),
    last_message_unix INTEGER,
    discovered_utc    TEXT NOT NULL,
    decided_utc       TEXT,
    PRIMARY KEY (source_id, chat_id)
) STRICT;

CREATE INDEX ix_sync_chat_decision ON sync_chat (source_id, decision);

-- How far a source has been read, per scope: one row per chat, plus '*' for account-wide update
-- state. The cursor is opaque to the schema — each connector owns its format.
--
-- Written in the same transaction as the page of messages it describes. A cursor committed ahead
-- of its messages skips them forever after a crash; one committed behind them re-reads a page,
-- which the uid makes harmless.
CREATE TABLE sync_state (
    source_id   TEXT NOT NULL REFERENCES import_source (id) ON DELETE CASCADE,
    scope       TEXT NOT NULL,
    cursor      TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (source_id, scope)
) STRICT;

-- Messages the platform says were deleted. The message itself stays exactly as it was (P2):
-- keeping what was said is what an archive is for. This row is what lets the conversation say
-- that it is no longer on the platform.
--
-- observed_utc is when the deletion was learned, not when it happened — no platform here reports
-- that — and the column is named so nobody mistakes one for the other.
CREATE TABLE message_deletion (
    message_id         INTEGER PRIMARY KEY REFERENCES message (id) ON DELETE CASCADE,
    source_id          TEXT NOT NULL REFERENCES import_source (id) ON DELETE CASCADE,
    observed_import_id TEXT REFERENCES import (id) ON DELETE SET NULL,
    observed_utc       TEXT NOT NULL
) STRICT;
