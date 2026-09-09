# Decisions

`message-archive-design.md` is the authoritative spec and is **not edited**. Where the
implementation departs from it, the departure is recorded here with the reasoning, so the
spec keeps reading as the original intent and this file carries the diff.

Each entry states the decision, why, and what would make us revisit it.

---

## D1 — Avalonia instead of Blazor

**Spec says:** "Blazor frontend". **We do:** Avalonia 11 with `CommunityToolkit.Mvvm`.

The app must be a standalone native tool on Windows, Linux and macOS. Blazor's viable
cross-platform hosts were a local Kestrel server viewed in a browser tab (works everywhere but
isn't an app, and cannot show a native folder picker for the import flow) or a WebView shell
(MAUI has no Linux support; Photino is thin but small-community). Avalonia is genuinely native
on all three targets.

**Costs accepted:** Telegram rich text must be built as an Avalonia `InlineCollection` rather
than rendered as HTML; FTS5 snippets are parsed into runs rather than `<mark>`; there is no
built-in media player (see D7).

**Revisit if:** the UI turns out to need HTML rendering fidelity we can't reach in Avalonia.

---

## D2 — No Docker

**Spec says:** "packaged via docker-compose". **We do:** self-contained per-RID publish.

V1 is steps 1-3 and needs no GPU, so the container existed only to package a desktop app —
which is the one thing containers are worst at. Compose returns later if and when the
transcription/inference stack (Whisper, a local LLM) needs orchestrating, which is exactly
how `adate` uses it: inference services in compose, app on the host.

---

## D3 — EF Core is the mapper; hand-written SQL owns the schema

**Decision:** numbered, embedded `.sql` migrations run by our own migration runner. No EF
migrations. `ArchiveDbContext` maps tables the SQL created; `EnsureCreated` is never called.

EF Core's SQLite provider cannot model FTS5 virtual tables, and its migration strategy for
altering a column or constraint is to rebuild the table — create temp, copy, drop, rename.
Dropping `message` drops its triggers, after which the FTS index stops being maintained and
search silently returns stale results rather than failing. Raw SQL inside `migrationBuilder.Sql()`
does not help, because the rebuild happens in the same migration and EF does not know to
recreate the triggers.

EF still earns its place for the entity model and shaped reads. A test
(`Ef_model_matches_the_sql_schema`) compares every mapped entity against `pragma table_info`,
which is what keeps "SQL is the authority" from becoming "the model has quietly drifted".

**Status:** implemented in M1.

---

## D4 — `message.id` is an INTEGER PRIMARY KEY

**Spec implies:** TEXT ids everywhere (house convention).

FTS5 external-content tables address the content table by `content_rowid`, and SQLite may
renumber rowids during `VACUUM` on a table without an explicit integer primary key. A single
`VACUUM` would silently decouple the search index from the messages.

Stable *identity* still lives in `message.uid` (TEXT, unique, deterministic,
`tg/<chat_id>/<message_id>`). That is the dedupe key and the citation target for the later
knowledge-base work; `message.id` is an internal join key only.

**Status:** planned for M1.

---

## D5 — Timestamps stored twice

`sent_at_utc` (ISO `"O"`, authoritative, house convention) alongside `sent_at_unix` (INTEGER,
the ordering and keyset-pagination key). Every hot index is `(..., sent_at_unix, id)`; keyset
paging over hundreds of thousands of rows on TEXT comparison is measurably worse. A test
asserts the two agree for every row.

**Status:** planned for M1.

---

## D6 — Saved Messages are imported as a conversation with yourself

The spec (§2) leaves this open. We import them (`thread.kind='saved'`, owner on both sides),
default on via `ImportOptions.SavedMessagesAsPerson`, because in a real export it is usually
the densest personal-notes archive present.

**Status:** planned for M3.

---

## D7 — No in-app media playback in V1

Avalonia renders images natively but has no audio or video player. V1 opens voice notes,
videos and files in the OS default handler (`Process.Start` with `UseShellExecute`).

**Revisit when:** transcription lands and checking a transcript against its audio becomes a
routine action. `LibVLCSharp` is the usual Avalonia answer and is a self-contained addition.

---

## D8 — FTS5 tokenizer: `unicode61`, not trigram

`unicode61 remove_diacritics 2` with `prefix='2 3'`, plus a query rewriter that appends `*` to
bare terms. There is no FTS5 stemmer for Slavic languages, and Slavic inflection is
overwhelmingly suffixal, so prefix expansion recovers most inflected matches cheaply.

Trigram would give inflection tolerance in any position but roughly triples index size and
cannot use `prefix`.

**Revisit when:** M7 measures prefix expansion against the real archive and finds it
insufficient. Adding `search_fts_trigram` over the same content table is a self-contained
migration, not a redesign.

---

## D9 — Correction to spec §8: the diary cache key

The spec keys a diary entry's cache on a hash of the message ids in its window. That is
insufficient: `superseded_by` means a fact learned in 2021 changes how 2019 should render, and
a message-only key would leave that entry stale forever.

The key must cover **the fact set visible to the window**, not just its messages. Combined with
the decision to regenerate visibly (an entry marked as revised, with a diff), the entry stores
the fact-set version that produced it.

**Status:** design only — no implementation before the knowledge-base phase.

---

## D10 — `media.extension`

The spec's `Media` table records mime, dimensions and duration but not the file extension.
V1 opens audio and video in the OS default handler (D7), and every desktop platform selects that
handler by extension — an extensionless file simply fails to open. The extension is therefore
part of the stored file's identity, not cosmetic, and the content-addressed path is
`media/ab/cd/<hash><ext>`.

---

## D11 — `message_import`: every import a message appeared in

**Spec has:** one import reference per message. **We add:** a link table recording every import
a message was seen in.

`message.first_import_id` is provenance — where the row came from — and never changes. That is a
different question from "which exports contain this message", which is what the UI needs in
order to filter the archive by source. The two diverge as soon as exports overlap, and
overlapping exports are the normal case: a fresh export of a chat you already imported contains
almost entirely messages you already have.

Two things this buys beyond the filter:

- **"What did this import actually add?"** is one indexed read, via the denormalized `is_first`
  flag, rather than a comparison against the whole archive.
- **An import becomes reversible.** The messages belonging only to import X are those with no
  other row in `message_import`, so an unwanted or mis-parsed import can be withdrawn without
  disturbing the rest of the archive.

The alternative — inferring the set from `first_import_id` alone — cannot answer either question,
because a message that arrived in three exports is recorded as belonging to one.
