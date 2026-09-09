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

## D11 — Sources and runs: a re-export is a new version, not a new import

**Spec has:** one import reference per message. **We have:** two levels — an
`import_source` (where data came from) and an `import` (one run of importing it) — with
messages linked to the source.

Re-exporting an account six months later does not produce a new source. It produces a newer
version of one. Modelling it as a second import had two costs:

- **Every re-import wrote one row per message.** A 500k-message archive paid 500k writes to
  record that almost nothing had changed. Keyed by source, a re-run inserts rows only for
  messages that are genuinely new; for the rest it is an index probe that writes nothing.
- **The filter was answering the wrong question.** "Show me what came from my Telegram" is
  about origin, not about how many times import was pressed. §9 provenance is likewise a
  property of a source, so `provenance` and an editable `label` live there.

`message_source.first_import_id` keeps the run-level answer available: what a particular run
added is one indexed read, which is what makes a bad run reversible.

### Which source an export belongs to is a question, not a deduction

`ImportRunner.Preview` reads only the export's personal_information block — a few kilobytes —
and returns the detected account, every existing source with its message count, a suggestion,
and the reason for it. The caller decides; `Run(..., sourceId:)` takes the answer.

The suggestion ladder, strongest first:

1. The export names its account and a source for it exists — extend it. Unambiguous.
2. The export names its account and no such source exists — create it.
3. The export names no account (a single-chat export) and exactly one source exists for the
   platform — suggest extending it. A save holds one person's archive, so this is usually
   right. Usually, not certainly, which is why it is a suggestion.
4. Otherwise — a source named after the folder, for the user to redirect.

Auto-detection must not decide on its own: only the user knows whether a folder is a fresh
export of their own account or an archive someone handed them that happens to overlap, and
merging those two conflates the provenance of two different people.

A label is written only when a source is created, so a re-run never renames a source the
user renamed.

---

## D12 — Import concurrency: the archive stays readable throughout

AGENTS.md P1 requires the archive to be usable while background work runs. For imports that
resolves into four concrete properties, each covered by a test in `ImportConcurrencyTests`:

**Readers are never blocked.** WAL gives one writer and unlimited concurrent readers. The
importer holds a write transaction for the duration of a batch, and readers on other connections
continue to be served from the last committed snapshot.
`The_archive_is_readable_while_an_import_is_writing` asserts reads complete in under a second
while a transaction is open — a blocked reader would instead wait out the 5-second
`busy_timeout` and then fail.

**Readers never see half a batch.** A batch is one transaction, so its messages appear together
or not at all.

**Batches land progressively.** With the default batch size an import becomes visible in
instalments rather than appearing all at once at the end, so a long import fills the archive in
front of the user.

**Nothing here touches a UI thread.** `ImportRunner.Run` is synchronous and does no marshalling;
the caller runs it on a background thread and marshals progress itself. The desktop head does
that in M4. Progress is reported per message and must be throttled by the consumer — at import
speed, an unthrottled UI update costs more than the import.

### The cost of a re-import

A re-import is cheap but not free, and the two costs are worth naming.

**Media is not re-hashed.** Storing a file means reading and hashing every byte, so resolving
attachments eagerly would mean re-hashing an entire media folder — tens of gigabytes on a real
archive — only to discover every message was already present. Media is therefore resolved
through a callback the committer invokes only when a message is new or revised.
`A_re_import_does_not_touch_the_media_store` holds that line.

**`message_source` is written only for new messages.** Keyed by source rather than by run
(D11), so an unchanged re-import performs one index probe per message and writes no rows at all.
A re-import is therefore proportional to what actually changed, not to the size of the export —
which, together with media no longer being re-hashed, is what makes re-running a large export
cheap enough to do routinely.

---

## D13 — A save is one person's archive

Every import into a save belongs to the same human. There is exactly one `is_owner` person, and a
platform account the importer has not seen before is attached to that person rather than becoming
a second owner — correct for someone with a personal and a work Telegram.

This is a design assumption, and it is load-bearing in two places:

**It is what makes "me" definite.** §7's knowledge base rests on the distinction between the
owner and everyone else — reflected evidence, the owner's cross-thread fact merge, the diary. A
save with two candidate owners has no coherent subject to write a diary about.

**It removes a collision that would otherwise be real.** A message uid is
`tg/<chat_id>/<message_id>`, and Telegram's chat id for a private chat is the *other* person's
user id — relative to whoever exported it. So "Sam's chat with Alex" and "my chat with Alex" both
carry chat id 5002. Two different people's archives in one save would collide on uid, and the
second import would silently record the collision as an edit of the first. Confined to one
person's accounts, chat ids are unambiguous and the collision cannot arise.

The residual case is one person with two accounts on the same platform, where two private chats
with the same third party would share an id. It is narrow, and the guard below surfaces the moment
it could occur.

**The guard.** `ImportPreview.AccountIsNewToOwner` is true when an export names an account that is
not yet one of the owner's, and the CLI warns before importing. The importer cannot tell a second
account of yours from someone else's archive, so it does not try — it says what it is about to do
and points at the alternative, which is §9's answer: a third-party archive is a different object
and belongs in its own save.
