-- Which build made this save, and which last changed its shape.
--
-- The schema itself already says what a save is: schema_migration lists what has run, and that is
-- what decides whether a save is behind, ahead or neither. What it cannot say is *whose* build did
-- it, which is the difference between "this save was made by a newer version of ahistory" and
-- "this save was made by ahistory 0.3.0 and you are running 0.2.0" — the second is actionable and
-- the first is a puzzle.
--
-- One row, like save_meta. Written by the application rather than by this file, because a
-- migration has no way to know what is running it.
CREATE TABLE save_provenance (
    id             INTEGER PRIMARY KEY CHECK (id = 1),

    -- Null on a save that predates this table: it was created before anything recorded this, and
    -- claiming otherwise would be inventing history.
    created_by     TEXT,
    created_utc    TEXT NOT NULL,

    -- The last build to apply a migration here, which is the one whose schema this now is.
    upgraded_by    TEXT,
    upgraded_utc   TEXT
) STRICT;

-- The row exists from the start so every later write is an update. strftime rather than a
-- parameter, because a migration runs without any.
INSERT INTO save_provenance (id, created_utc)
VALUES (1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));
