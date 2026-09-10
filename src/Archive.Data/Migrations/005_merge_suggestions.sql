-- Suggestions the user has already said no to.
--
-- Matching two accounts on a display name is a guess, and §1 is emphatic that auto-matching gets
-- it wrong — an under-merge is untidy, an over-merge is a confident lie about who said something.
-- So suggestions are never applied; they are offered. Which means the same wrong pair is offered
-- again on every visit unless the answer is remembered, and a list that keeps re-asking is one
-- people stop reading.
--
-- Only the rejections are stored. An accepted suggestion needs no row: the identities are on one
-- person afterwards, and the suggestion no longer generates.
--
-- Keyed by identity pair rather than by person pair, because people come and go as merges happen
-- while an identity is permanent (§1: merging repoints identities, it never rewrites anything).
-- The pair is stored in a fixed order so "A with B" and "B with A" are one row.
CREATE TABLE merge_dismissal (
    left_identity_id  TEXT NOT NULL REFERENCES identity (id) ON DELETE CASCADE,
    right_identity_id TEXT NOT NULL REFERENCES identity (id) ON DELETE CASCADE,
    dismissed_utc     TEXT NOT NULL,
    PRIMARY KEY (left_identity_id, right_identity_id),
    -- The ordering is a constraint rather than a convention, so a row inserted the other way round
    -- fails loudly instead of becoming a duplicate nothing matches against.
    CHECK (left_identity_id < right_identity_id)
) STRICT;

CREATE INDEX ix_merge_dismissal_right ON merge_dismissal (right_identity_id);
