> **Historical record.** This is the V1 implementation plan as approved on 2026-09-09, before
> any code existed. It is kept unedited, so it reads as the plan actually agreed to rather than
> as a description of what was built.
>
> The implementation has since moved past it in several places — `session` moved into
> `001_core.sql`, `message_import` became the `import_source` / `message_source` split, and
> `media.extension` was added. **[docs/decisions.md](decisions.md) is authoritative** wherever
> the two disagree, and records why each departure was made.

---

# ahistory — V1 plan (message archive)

## Context

`C:\git\ahistory` currently holds one file: `message-archive-design.md`, a design spec for a
local-first personal message archive — imports chat takeouts, merges platform identities into
human persons, shows one continuous conversation per person, and eventually builds an
LLM-derived knowledge base and diary with citations.

Nothing is built. This plan turns the spec's build-order steps **1–3** into an executable V1,
and locks the schema seams that the later knowledge-base work depends on so they don't need a
painful retrofit.

The spec stays the authoritative document, cited by section number (`§2`, `§6.4`) in code
comments. Anywhere this plan departs from it, the departure is recorded in `docs/decisions.md`
rather than by editing the spec — the convention already used in `C:\git\adate`.

### Decisions taken in this session

| Question | Decision |
|---|---|
| V1 scope | Build-order steps 1–3 only: schema + Telegram importer + media store, per-person thread view, FTS5 search. No jobs queue, no Whisper/OCR, no embeddings, no LLM. |
| Packaging | Standalone .NET app, **no Docker**. (V1 needs no GPU, so the CUDA-in-container question disappears entirely.) |
| UI framework | **Avalonia**, not Blazor — a genuinely native desktop app on Windows, Linux and macOS. This departs from the spec's stated "Blazor frontend"; recorded in `docs/decisions.md`. |
| Target platforms | Windows, Linux and macOS all first-class. |
| Data access | EF Core as the entity mapper, raw SQL for FTS5 and hot queries — **with one amendment, see "Migrations" below.** |
| Schema reach | Archive tables + extension seams + **session segmentation** (cheap, no LLM, improves search immediately). |
| Fact identity | Embedding-based candidate generation + a small local-model adjudication call when similarity is ambiguous. |
| Fact history | **Append-only.** Nothing is ever updated in place; superseded facts are closed out and point at their replacement. |
| Diary regeneration | Regenerate when new data lands, and **show the reader that it was revised**, with a diff. |
| Fact provenance | Citations carry **roles** — which message asserted it, corroborated it, contradicted it, and specifically which one established each validity date. |

House conventions come from `C:\git\adate`, the closest sibling (Blazor Server + SQLite +
`STRICT` tables + numbered `.sql` migrations + hand-written repositories).

---

## 1. Solution layout

```
Ahistory.slnx                      (.slnx, not .sln)
Directory.Build.props              net10.0, nullable, TreatWarningsAsErrors, InvariantGlobalization
global.json                        SDK 10.0.100, rollForward latestFeature
message-archive-design.md          authoritative spec
docs/decisions.md                  departures from the spec, with reasoning
src/
  Archive.Core/     domain records, ids, options contracts. Depends on nothing.
  Archive.Data/     Database (connection + pragmas + migration runner), embedded .sql,
                    ArchiveDbContext, raw-SQL query objects
  Archive.Media/    content-addressed blob store
  Archive.Import/   Telegram streaming reader, normalizer, committer, ImportRunner
  Archive.Cli/      headless `import` / `init` — proves M1–M3 without a UI
  Archive.Ui/       Avalonia views + view models, platform-agnostic. All the UI lives here.
  Archive.Desktop/  the thin desktop head: AppBuilder, DI wiring, single-instance, publish targets
tests/
  Archive.{Core,Data,Media,Import}.Tests/
  Archive.Ui.Tests/                Avalonia.Headless.XUnit
  fixtures/telegram/<case>/        golden export fixtures
```

Dependency rule (from `adate\HANDOFF.md` §3): `Core` depends on nothing, everything depends on
`Core`, the heads depend on everything.

`Archive.Cli` exists because V1's headline acceptance criterion — a real multi-hundred-MB export
re-imports as a no-op — must be provable headlessly, and because it keeps M1–M3 demonstrable
before any UI exists.

The `Ui` / `Desktop` split is not ceremony: it keeps every view and view model free of
platform-head concerns, so the headless UI tests instantiate view models without an
`AppBuilder`, and so a second head (a future mobile or embedded viewer) is a new project rather
than a refactor.

**UI stack:** Avalonia 11 with `CommunityToolkit.Mvvm` (`ObservableObject`, `RelayCommand`,
source-generated) — the current Avalonia template default, and lighter than ReactiveUI for what
this app does. `Microsoft.Extensions.Hosting` + DI hosts the same services the CLI uses, so
`ArchiveOptions` / `ImportOptions` / `SearchOptions` and the `PostConfigure`-validates-and-throws
convention survive intact. Config resolves from `appsettings.json` beside the executable, then a
per-user config directory, then `AHISTORY_`-prefixed environment variables — the env prefix stays,
but a desktop app can't rely on it as the primary channel.

Options are sealed classes with `const SectionName`, bound via `services.Configure<>`, env prefix
`AHISTORY_` with `__` nesting, **validated in `PostConfigure` and thrown at startup**:
`ArchiveOptions` (db path, media dir), `ImportOptions` (batch size, `StoreRawJson`,
`SavedMessagesAsPerson`), `SearchOptions`.

---

## 2. Schema

`STRICT` tables, TEXT ids, ISO `"O"` UTC timestamps, `ON DELETE CASCADE`, raw string literals
(`"""`) for embedded SQL, comments citing spec sections and the failure mode being avoided.

**Core tables** (`001_core.sql`): `save_meta` (single row; `owner_is_self` + provenance note, §9),
`import`, `person`, `identity`, `identity_person`, `thread`, `thread_participant`, `media`,
`message`, `message_media`.

Three additions the spec omits but Telegram exports contain:

- **`message_revision`** — a later export can carry different text for the same message id. §1 says
  messages are immutable after import, so a changed body appends a revision row and never loses
  the original.
- **`reaction`** — with a unique index on `(message_id, emoji, actor)` so re-import is a no-op
  rather than a duplicate storm. Reactions are also strong behavioral signal for §7.
- **Forward metadata** on `message` (`forwarded_from`, `forwarded_at_utc`, `via_bot`).

Two deliberate departures, both recorded in `docs/decisions.md`:

1. **`message.id` is `INTEGER PRIMARY KEY`, not TEXT.** FTS5 external-content tables address the
   content table by `content_rowid`, and SQLite may renumber rowids during `VACUUM` on tables
   without an explicit integer PK — a single `VACUUM` would silently decouple the search index
   from the messages. Stable *identity* lives in `message.uid` (TEXT, unique, deterministic,
   `tg/<chat_id>/<message_id>`), which is the dedupe key and the citation target.
2. **Timestamps stored twice** — `sent_at_utc` (ISO, authoritative, house convention) and
   `sent_at_unix` (INTEGER, the ordering/keyset key). Every hot index is
   `(…, sent_at_unix, id)`; keyset paging over 500k rows on TEXT comparison is measurably worse.
   A test asserts the two agree for every row.

`ux_person_owner`, a partial unique index on `is_owner = 1`, makes a second owner impossible at the
storage layer — §1 warns that merging a contact into the owner poisons the knowledge base, so it
should be unrepresentable, not just guarded in the merge UI.

**Search layer** (`003_search.sql`): a `search_document` table with a `provenance` column
(`message` | `transcript` | `ocr` | `caption`) and an FTS5 external-content virtual table over it,
kept in sync by triggers. V1 writes only `provenance='message'` rows. The indirection is what lets
transcripts join the same searchable surface later by *inserting rows*, with no re-index and no
rewrite of the search queries or result components — and it's what §3's "flag provenance on the
search index row" requires.

**Pragma block** — `adate`'s four plus one:

```
journal_mode=WAL; synchronous=NORMAL; foreign_keys=ON; busy_timeout=5000;
recursive_triggers=ON;
```

`recursive_triggers` is not optional here: rows deleted by an `ON DELETE CASCADE` do **not** fire
delete triggers without it, so deleting a thread would leave orphaned FTS rows that still match
queries. Covered by `Deleting_a_thread_removes_its_messages_from_the_index`.

---

## 3. Migrations — amendment to the EF Core decision

**Recommendation: EF Core is the mapper; hand-written numbered `.sql` files are the schema
authority. No EF migrations.** This is a departure from the data-access answer as literally stated,
so it needs an explicit yes.

Why: EF's SQLite provider cannot model virtual tables at all, and its migration strategy for column
or constraint changes is *rebuild the table* — create temp, copy, drop, rename. Dropping `message`
drops its triggers, and the FTS index then silently stops being maintained. Search keeps returning
stale results instead of failing, which is the worst available failure mode. Putting raw SQL in
`migrationBuilder.Sql()` doesn't rescue it, because the rebuild happens in the same migration and
EF doesn't know to recreate the triggers.

The migration runner is lifted from `adate\src\Game.Data\Database.cs` (embedded `.sql`, filename
order, `schema_migration` table, one transaction each), plus: `foreign_keys=OFF` before `BEGIN`
with `PRAGMA foreign_key_check` before `COMMIT`, so a migration leaving dangling references fails
loudly; and a `SchemaFingerprint()` helper over `sqlite_master` for tests.

EF still earns its place for the entity model and shaped reads:
`AddDbContextFactory<ArchiveDbContext>` (not `AddDbContext` — a desktop app has no request scope
at all, and view models are long-lived, so a shared context would be a concurrency bug waiting to
happen), a `DbConnectionInterceptor` applying the same pragma block so EF-opened and
hand-opened connections are indistinguishable, `NoTracking` globally. A test
`Ef_model_matches_the_sql_schema` compares every mapped entity against `pragma table_info`, which
is what makes "SQL is authority, EF is mapper" safe rather than a drift generator.

---

## 4. Import pipeline

Staging → normalize → dedupe → commit (§2). Progress lives in an in-process `ImportRunner`
exposing an observable progress record that the import view model subscribes to — no jobs table in
V1. The runner does its work on a background thread and marshals progress to the UI thread via
`Dispatcher.UIThread.Post`; nothing touching SQLite ever runs on the UI thread.

**Streaming:** chunked `Utf8JsonReader` over a `FileStream`, carrying `JsonReaderState` across
buffer refills. Never materializes the document; peak memory is the buffer plus one batch,
regardless of a 2 GB export. Each message's exact UTF-8 byte slice is stored verbatim as
`raw_json` (§1: re-run rather than re-ask for exports). Multi-file exports (`result.json`,
`result2.json`, …) are one logical import with one `import` row.

**The §2 traps, handled explicitly:**

- `text` is ignored entirely — `plaintext` comes from concatenating `text_entities[].text`, with
  the entity array kept in `entities_json` for rendering. A fixture asserts that a message whose
  `text` and `text_entities` disagree resolves to `text_entities`.
- `from_id` prefixes are stripped into `(kind, id)`; an **unknown prefix throws** rather than
  silently becoming part of the id — a new prefix means an identity class we haven't considered.
- `type: "service"` routes through a separate path using `actor`/`actor_id`, so no phantom person
  named "phone call".
- `personal_information` seeds the single owner Person with `confidence='seed'`; if an owner
  already exists, the new identity links to it.
- Saved Messages import as a `thread.kind='saved'` conversation with yourself (default on) —
  answering the spec's open question, recorded in decisions.md.
- Name-only senders become `is_synthetic` identities, unique on `(platform, display_name)`,
  surfaced in the merge UI as low-confidence.
- The "(File not included…)" sentinel becomes a `message_media` row with a null hash and a
  `missing_reason`, never a failure.

`reply_to_message_id` is stored as a **uid string, not an FK**, because export order isn't
topological and a reply can precede its target across files. Resolution is a read-time join.

**Media:** hash while streaming to a temp file, then `File.Move(overwrite: false)` into
`media/ab/cd/<hash><ext>`; catching the already-exists case *is* the dedupe — no `File.Exists`
race. Stickers and forwards repeat hundreds of times per export and get written once.

**Idempotency is enforced by the storage layer, not by pre-checking** (a pre-check is a race and
doubles the reads): `INSERT … ON CONFLICT DO NOTHING` on every natural key, with
`ON CONFLICT(uid) DO NOTHING RETURNING id` on messages. Nothing returned means the message exists;
equal `content_hash` skips, different `content_hash` appends a `message_revision` and updates.
Commits go through prepared `SqliteCommand`s in batches, not `SaveChanges` — 500k rows through the
change tracker is minutes instead of seconds. `import.stats_json` records the counts, so a clean
re-import reads `inserted == 0, skipped == seen`.

---

## 5. Avalonia UI (V1)

Avalonia 11, MVVM via `CommunityToolkit.Mvvm`, a single main window with a navigation sidebar —
a desktop tool, not a page-routed web app. Views are plain AXAML with a hand-written theme; no
control library beyond Avalonia's own Fluent theme.

| View | Purpose |
|---|---|
| Overview | Save summary: counts, last import, unmerged identities |
| Import | **Native folder picker** via `IStorageProvider.OpenFolderPickerAsync`, live progress, result stats |
| People | Person list, merge/unmerge; **merging into the owner is a separate confirm dialog** (§1) |
| Person | The continuous conversation — the centrepiece |
| Thread | Real thread, anchored at a message — target of "open in context" |
| Search | FTS5 results with snippets, filters, provenance badges |

**The per-person union query** (§4) is generated as one index-range-scan arm per DM thread and per
identity, `UNION ALL`-ed, with the keyset predicate and `LIMIT` pushed **inside each arm**. A
single outer `WHERE`/`LIMIT` over the union makes SQLite materialize both arms in full — the
difference between milliseconds and seconds at 500k rows. The cursor is `(sent_at_unix, id)`; the
`id` tie-break is mandatory because Telegram timestamps collide at second granularity constantly
and a timestamp-only cursor drops or repeats rows at page boundaries. A test asserts the
`EXPLAIN QUERY PLAN` contains no `SCAN message` and no `USE TEMP B-TREE`.

**Virtualization:** a `ListBox` (or `ItemsControl`) with `VirtualizingStackPanel` over an
`ObservableCollection<MessageRow>` that grows at the head. Older pages are requested by watching
`ScrollViewer.Offset` and prefetching when the viewport nears the top — the native equivalent of
the sentinel/`IntersectionObserver` trick, and simpler because there is no JS interop boundary.

Two Avalonia-specific traps to handle deliberately:

- **Prepending items scrolls the view.** Inserting older messages at index 0 shifts everything
  down and the reading position jumps. Capture `Offset` and extent before the insert and restore
  the delta after layout (`Dispatcher.UIThread.Post` at `Render` priority), or the history scroll
  feels broken.
- **Variable-height items break scroll estimation.** Chat rows differ wildly in height, so the
  scrollbar will jitter as items realize. Acceptable for V1; note it rather than fight it.

**Rich text is the real cost of going native.** Telegram `entities_json` (bold, italic, code,
links, mentions) rendered as HTML in a browser for free. In Avalonia it becomes a `TextBlock`
with a built `InlineCollection` — `Run`, `Bold`, `Italic`, `Span` — plus custom pointer handling
for links, since Avalonia has no hyperlink inline. A small `EntityInlineBuilder` in `Archive.Ui`
converts entities to inlines and is unit-tested on its own. The same builder renders search
snippets: `FtsSnippetParser` turns FTS5's marker output into highlighted runs instead of the
`<mark>` HTML the Blazor design assumed.

**Media playback.** Avalonia renders images natively (`Bitmap` from the content-addressed path),
but has **no built-in audio or video player**. V1 does not embed one: voice notes, videos and
files open in the OS default handler via `Process.Start(UseShellExecute: true)`. If in-app
playback becomes important — and it will, once transcription lands and you want to check a
transcript against the audio — `LibVLCSharp` is the usual Avalonia answer and is a self-contained
addition. Recorded in `docs/decisions.md` as a deferred choice with its trigger.

**Group context** (§4): group-origin rows render dimmed with the group title and an expand
affordance; expanding runs two bounded keyset queries ±8 messages around it *in its real thread*.
No per-participant row copies anywhere.

**Silence dividers** ("4 months, no contact") render between rows more than a threshold apart. §8
calls this often the most meaningful thing in a timeline; it's free from data already loaded, and
doing it now means the diary layer inherits a rendering convention instead of inventing one.
Note for later: the threshold must eventually be **relative to that pair's own baseline cadence** —
four months is enormous for a daily correspondent and meaningless for a yearly one.

---

## 6. Search

**Tokenizer: `unicode61 remove_diacritics 2` with `prefix='2 3'`.** The archive is multilingual
including Ukrainian/Russian. There is no FTS5 stemmer for Slavic languages (`porter` is
English-only), so `Прага` / `Праге` / `Прагу` won't match on exact tokens. Slavic inflection is
overwhelmingly suffixal, so a prefix index plus a query rewriter appending `*` to bare terms
recovers most of it cheaply.

**Trigram is deliberately deferred**, not rejected: it would give inflection tolerance in any
position but roughly triples index size and can't use `prefix`. If prefix expansion proves
insufficient against the real archive at M7, a second virtual table over the same content table is
a self-contained migration. Recorded in decisions.md with its trigger condition.

**Never pass raw input to `MATCH`.** `FtsQueryBuilder` splits on whitespace, preserves quoted
phrases, escapes embedded quotes, wraps bare terms as `"term"*`, joins with `AND` — neutralizing
FTS5 operator syntax and adding prefix expansion in one step. Ranking is `bm25` (negative scores,
ascending). Snippets come back from `snippet()` with delimiter markers and are parsed into
highlighted inlines by `FtsSnippetParser` — with a marker string that cannot occur in message text,
and a parser that degrades to plain text rather than throwing if it ever does.

---

## 7. Tests

xUnit v2, plain `Assert.*`, no FluentAssertions/Moq/NSubstitute (hand-written fakes as nested
classes), snake_case sentence method names. `TempDatabase` copied from
`adate\tests\Game.Data.Tests\TempDatabase.cs` — a real SQLite **file** in a GUID temp dir so WAL,
FK and migration behavior are real, with `SqliteConnection.ClearAllPools()` on dispose for Windows
file locks.

**Golden importer fixtures** in `tests/fixtures/telegram/<case>/`, located by walking up from
`AppContext.BaseDirectory` to the `.slnx` (the `ShippedContentTests` pattern). Each is a miniature
export plus an `expected.json` **digest** — every table dumped in stable sort order with volatile
columns normalized — so a failure names the diverging row instead of "counts differ". One case per
§2 trap: entity-vs-text disagreement, rich entities, service messages, prefixed and unknown ids,
owner seeding, saved messages, group+DM for the union view, reactions and edits, forwards with a
repeated sticker (dedupe), missing media, multi-file, name-only sender.

**The headline test — `Re_importing_the_same_export_changes_nothing`:** import, snapshot the
full-database digest, import the identical folder again, assert the digest is byte-identical, that
`import` has two rows, that the second reports `inserted == 0 && skipped == seen`, and that the
media directory's file count and total bytes are unchanged. Variants cover a superset re-import and
an edited message becoming a revision rather than a duplicate.

Plus: migration idempotency and schema-fingerprint stability, the EF-model-vs-SQL-schema check,
pragma assertions, the full trigger suite including the cascade case, keyset paging across a
timestamp tie, query-plan assertions, Cyrillic case-folding and prefix-expansion search tests, and
a `[Trait("Category","Perf")]` run at 200k synthetic messages asserting first page and search both
under 100 ms.

**UI tests** (`Archive.Ui.Tests`) use `Avalonia.Headless.XUnit` — it runs a real Avalonia app
without a display, so views can be instantiated and driven in CI on any of the three platforms.
Most value is in plain view-model tests (no headless harness needed) plus focused headless tests
for the two things that are pure UI logic and easy to get wrong: `EntityInlineBuilder` producing
the right inline tree from `entities_json`, and `FtsSnippetParser` handling a snippet whose text
contains the marker sequence.

---

## 8. Milestones

Each ends with the app runnable.

- **M0 Skeleton** — solution, props, projects wired per the dependency rule, `docs/decisions.md`
  seeded. *Green build and test run.*
- **M1 Storage** — `Database` with pragmas and the migration runner, all three migrations, EF
  context + interceptor, options. *`Archive.Cli init` creates a migrated save.*
- **M2 Media store** — content-addressed put, dedupe, crash-safety tests.
- **M3 Telegram import** ← *the milestone that matters.* Streaming reader, normalizer with every
  §2 trap, ON-CONFLICT committer, all fixtures, the idempotency test. Then run it against the real
  export and record throughput, database size and `raw_json` cost in decisions.md.
- **M4 Desktop shell + identity merging** — Avalonia app, DI/host wiring, main window and
  navigation, Overview, Import (native folder picker, live progress), People with the owner-merge
  confirm, Thread view with plain paging.
- **M5 Continuous conversation** — union query, virtualized list with head-insertion and scroll
  anchoring, `EntityInlineBuilder`, group context, silence dividers, anchored jump.
  *Spec build-order step 2 complete.*
- **M6 Search** — query builder, snippet parsing to inlines, filters, provenance badges.
  *Step 3 complete; V1 is feature-complete.*
- **M7 Packaging + hardening** — publish profiles for all three RIDs (see §11), perf pass on the
  real archive, decide the trigram question with real data, finalize decisions.md and README.

M1→M2 overlap; M4 can start once M1 lands; M5 needs M3's real data.

---

## 9. Seams for the knowledge base (created in V1, unused in V1)

`002_derived.sql` ships these empty. V1 code touches only `message.session_id` (always null) and
the `search_document.derived_artifact_id` branch (always null). Each is cheap now and expensive or
impossible to retrofit.

**(a) Stable citable identity — the load-bearing one.** §6.4 and §8 make a diary window's cache key
a hash of the message ids it covers, and every fact carries message ids. If ids aren't stable
across re-import, every re-import invalidates the whole tree and every stored citation. That's
`message.uid`, present from the first migration.

**(b) `person_edge`** — relational facts live on the edge, not the node (§7). One row per unordered
pair (`CHECK (person_a_id < person_b_id)`), so "met in Berlin in 2015" can't exist twice with two
different stories. The V1 consequence: a fact's subject must be addressable as *person XOR edge*
from the start; adding edges later means migrating every fact and every query.

**(c) `fact` + `fact_citation`, append-only and bitemporal.** Facts are never updated in place —
a correction is a new row, and the old one is closed out with `superseded_by`. Two time axes:
event time (`valid_from_utc`/`valid_to_utc` — when the claim was true of the world) and assertion
time (`asserted_utc`/`retracted_utc` — when we came to believe it). Both are needed so the diary
narrating 2019 doesn't quietly use knowledge that arrived in 2023.

`fact_citation` carries a **`role`** — `asserts`, `corroborates`, `contradicts`,
`establishes_valid_from`, `establishes_valid_to` — and `fact.valid_from_utc` / `valid_to_utc` each
reference the citation that established them. This is the "track every message that influenced the
decision, including the validity dates" requirement: without roles you can see a date but never
audit where it came from, which is exactly what you'd want to check when a date looks wrong.

Facts also carry `evidence_kind` (self-report / reflected / behavioral, §7) and `origin_kind`
(DM vs group, §4 — "Sam mentioned he's moving" is much weaker said to eleven people).

**Fact merging** (deferred to its phase, designed now): embeddings retrieve candidate matches, and
a small **local** model adjudicates the ambiguous band with three outcomes — *same*, *different*,
or *replaces*. The third is the one neither embeddings nor key-matching can produce, and it's what
writes `valid_to` on the old fact and `valid_from` on the new one. Identical normalized keys can
short-circuit to an auto-merge for free. The reason adjudication can't be skipped at high
similarity: "Sam works at Acme" and "Sam is leaving Acme" are nearly identical as vectors, and an
under-merge is merely ugly while an over-merge is a confident lie with twelve citations behind it.

**(d) `session`** (§6.1, "the most important step") — thread-scoped, time-gap segmented, with a
`member_hash` cache key and a `segmenter_version`. V1 ships `message.session_id` and its index so
the segmenter writes one column rather than altering a 500k-row table. Note for the AI phase:
sessions belong to a **thread**, while the person view is a union across threads — the segmenter
runs per thread, and the person view composes sessions rather than defining them.

**(e) `derived_artifact`** — one table for every machine-produced text (transcript, OCR, caption,
embedding, session extract, rollup, diary entry), because §3's rule is that derived text hangs off
its source and never replaces it, and §6.6 needs "re-run only sessions processed with prompt < v4"
to be a one-line query. Carries `model_version`, `prompt_version`, `input_hash` (§6.4's cache key),
and `confidence`. Re-running a model inserts a new row, so diffing old against new is a query.

**Diary caching — a correction to §8.** The spec keys a diary entry's cache on a hash of the
message ids in its window. That's insufficient: `superseded_by` means a fact learned in 2021
changes how 2019 should render, and a message-only key would leave that entry stale forever. The
key must cover **the fact set visible to the window**, not just its messages. Combined with the
"regenerate and show a diff" decision, a revised entry is marked as revised and can show what
changed and why — the entry stores the fact-set version that produced it.

**Citation enforcement — what's actually checkable.** Verifying that cited message ids exist and
fall inside the window is mechanical and cheap; do it and drop anything failing it. Verifying that
a sentence *follows* from those messages is not mechanical. "Drop anything without citations"
catches the lazy failure, not the confident one — which is the corrosive case §8 names.

---

## 10. Packaging (M7)

There is no single artifact that runs everywhere — self-contained publish is **per-RID**, and each
OS needs its own container format:

| Target | Command / format | Caveat |
|---|---|---|
| Windows | `-r win-x64 --self-contained` → single `.exe` | Unsigned binaries trip SmartScreen; code signing needs a certificate. |
| Linux | `-r linux-x64 --self-contained` → tarball, then AppImage or `.deb` | Avalonia needs the usual X11/fontconfig libs; test on a clean container. |
| macOS | `-r osx-x64` / `osx-arm64` → `.app` bundle | **Producing signed, notarized builds requires a Mac with Xcode.** Both architectures, or a universal bundle. |

Two constraints to plan around:

- **Ship untrimmed.** Avalonia tolerates trimming and NativeAOT reasonably well, but EF Core's
  reflection does not. Expect roughly 70–100 MB per platform and don't spend M7 fighting it.
- **The save is portable, the app is not.** A `.db` plus its media folder must open on any of the
  three platforms — so no absolute paths inside the database (media is addressed by hash, and the
  media root comes from configuration), and the path-handling code must never assume a separator.
  A test opens a fixture save through a path containing both separator styles.

macOS is the one to sequence honestly: build and test Windows and Linux in M7, and treat the
notarized macOS bundle as a separate task gated on having a Mac available. Everything else about
the app is platform-agnostic from day one.

---

## 11. Verification

- `dotnet build Ahistory.slnx` and `dotnet test Ahistory.slnx` green after every milestone;
  `TreatWarningsAsErrors` means warnings block.
- **M3 gate:** `Archive.Cli import <real-export>` against the actual Telegram export, then the same
  command again. Second run must report `inserted == 0, skipped == seen`, and the database digest
  and media directory must be unchanged. Record throughput, final `.db` size and the `raw_json`
  overhead in `docs/decisions.md`.
- **M5 gate:** open the Person view for the busiest contact, scroll back through several thousand
  messages, expand a group-context row, jump to a message and back. The scroll position must not
  jump when an older page loads. `EXPLAIN QUERY PLAN` tests must stay green.
- **M6 gate:** search a Cyrillic term in an inflected form and get the base-form messages; search a
  string containing FTS operator characters and get results rather than an exception; every result
  shows a provenance badge.
- Perf trait run at 200k synthetic messages: first person-view page and search both under 100 ms.
- **M7 gate:** self-contained publish for `win-x64` and `linux-x64`, each launched on a clean
  machine or container with no .NET installed, opening the same save file copied across — the
  archive must render identically. macOS bundle verified separately when a Mac is available.
