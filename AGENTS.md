# Working in this repository

`ahistory` is a local-first personal message archive. It imports chat takeouts, merges platform
identities into human persons, and shows one continuous conversation per person with full-text
search. Everything stays on the machine it runs on.

Read [`message-archive-design.md`](message-archive-design.md) first — it is the authoritative
spec, cited by section number (`§2`, `§6.4`) throughout the code. Every departure from it is
recorded in [`docs/decisions.md`](docs/decisions.md), with reasoning. **Do not edit the spec to
match the implementation.** Record the departure instead; the spec keeps reading as the original
intent, and the decisions file carries the diff.

---

## P1 — It is a chat history app first. The AI layer is optional.

This is the principle that outranks the others, and the one most likely to be eroded by a
reasonable-sounding change.

The archive must be **completely usable with no AI component present, installed, or running**.
Transcription, OCR, embeddings, extracted facts and the diary are enrichments layered on top of
a working archive — never preconditions for it. A user who never enables any of it should get a
fast, complete, searchable history and never encounter a disabled button or an empty panel
explaining what they are missing.

It follows that the archive must also stay usable **while** that work runs. Enrichment happens
in the background, for hours, over hundreds of thousands of messages. Reading history during
that time is the normal case, not a degraded one.

Concretely, and non-negotiably:

- **Switched off, AI leaves no trace in the window.** No model, no process, no network call, and
  nothing of it in the rail — not a disabled entry, not an empty page explaining what is missing.
  The settings page is the single exception, because it is where the switch is. Enforced by
  `AiPageTests`.
- **Turning it off is complete.** Every derived row can be deleted in one action, and what is left
  is exactly the archive that was there before.
- **No model runtime in the storage layer.** `Archive.Core`, `Archive.Data`, `Archive.Import`,
  `Archive.Media` and `Archive.Logging` must not reference an inference or embedding package, and
  must not reference `Archive.Ai`. The UI and the heads may — AI there is a page and a switch
  (decisions.md D31). What this protects is that `sqlite-vec` and `Whisper.net` are native,
  per-RID binaries which would otherwise ship and load for every user who never enables anything.
  Enforced by `SolutionLayoutTests.No_storage_project_takes_an_ai_dependency` and
  `No_storage_project_references_the_ai_project`.
- **No view may require a derived artifact to render.** A message with no transcript, no
  embedding and no extracted facts is the normal state and must look normal — not like a loading
  skeleton that never resolves.
- **Search works with zero embeddings.** FTS5 keyword search is the baseline capability. Semantic
  and hybrid ranking improve it; they are never what makes it function.
- **Never hold a write transaction across a model call.** SQLite allows one writer. An LLM call
  can take thirty seconds; a transaction open across one stalls every other write in the app and
  blocks WAL checkpointing. Compute first, then open a short transaction and commit.
- **Background writes are small and batched.** Bounded batches with short transactions, so a
  reader is never waiting on a long-running enrichment job.
- **Enrichment is pausable and resumable**, and stopping it leaves a fully working archive.

When a change would make some part of the history unavailable, slower, or conditional on
AI processing having finished — that change is wrong, however convenient it is.

---

## Other principles

**P2 — Imported data is never rewritten.** Message rows are immutable after import. A changed
body on re-import becomes a `message_revision`; a corrected fact becomes a new `fact` row with
the old one closed out. The original export file and per-message `raw_json` are kept forever, so
a parser gap is re-run rather than re-requested from the user.

**P3 — Re-import is idempotent.** Importing the same export twice must change nothing. This is
the headline acceptance criterion for the importer and is enforced by
`Re_importing_the_same_export_changes_nothing`.

**P4 — Keep the app runnable at every milestone boundary.** Each milestone in the README ends
with something demonstrable, not with a half-wired layer.

**P5 — A save is one person's archive.** Every import belongs to the same human; a
platform account the importer has not seen before attaches to the existing owner rather than
becoming a second one. **An owner is never invented** (D25): a format that does not state its
account — VK, QIP — says so, offers what it found, and asks. What it cannot do is make one up and
store it as though the export had named it, which left a user's real account arriving as an
ordinary contact and their own history file reading as a conversation with themselves (D26). This is what makes "me" definite for the knowledge base, and it is what
keeps message uids unambiguous — Telegram private-chat ids are relative to whoever exported
them, so two different people's archives in one save would collide. An archive someone gave you
belongs in its own save (§9, decisions.md D13).

**P6 — A log must be safe to attach to a bug report.** The archive is people's private
correspondence, and a log that quotes it is as sensitive as the archive itself while being far
more likely to be copied somewhere else. Log **counts, identifiers, durations and error types.
Never content, never names** — no message text, no chat or contact display names, no entity or
raw export JSON, no search terms. Refer to things by id: `telegram:100` tells a diagnostic
everything and a stranger nothing. The one deliberate exception is the export folder path,
logged once per import, because the user chose it and an import that cannot say where it read
from is very hard to diagnose. Enforced by `LoggingPrivacyTests` against a real import.

**P7 — The save is portable, the app is not.** A `.db` plus its media folder must open on
Windows, Linux and macOS. No absolute paths in the database, no assumed path separator.

---

## Build and test

```
dotnet build Ahistory.slnx
dotnet test Ahistory.slnx
```

Warnings are errors. Fix them; do not suppress them without a comment saying why.

Perf tests are excluded from a normal run with `--filter Category!=Perf`.

The CLI is how storage and import work is demonstrated without a UI:

```
dotnet run --project src/Archive.Cli -- init <path-to-save.db>
```

---

## Conventions

**Structure.** `Core` depends on nothing; everything depends on `Core`; the heads (`Cli`,
`Desktop`) depend on everything. Enforced by `SolutionLayoutTests`.

Shared build settings live in `Directory.Build.props` and are **not** repeated in any csproj —
also enforced by a test.

**The SQL owns the schema; EF Core is a mapper** (decisions.md D3). Schema changes are new
numbered `.sql` files in `src/Archive.Data/Migrations/`, embedded in the assembly and applied in
filename order. There are no EF migrations and `EnsureCreated` is never called. After adding a
column, map it in `ArchiveDbContext` — `EfSchemaTests` checks both directions and will fail if
you map a column that does not exist or add one you did not map.

**Migrations are append-only. Never edit one that has shipped** (decisions.md D24). A save records
migrations by *filename*, so it has no memory of what the file said when it ran: editing an applied
migration changes what new saves get and leaves every existing save behind, undetectably. It has
happened once already, and it surfaced as a `NOT NULL` failure on a column the code no longer
mentioned. `MigrationTests` pins the SHA-256 of every migration that has shipped — add a line when
you add a migration; **changing a hash to make that test pass is the bug it exists to catch.**
Schema only ever moves forward: there is no rollback and no support for a diverged save.

**Every connection goes through `Database`.** It applies the pragma block, including
`recursive_triggers`, without which cascaded deletes fire no triggers and the FTS index silently
retains rows for messages that no longer exist.

**Tables are `STRICT`.** TEXT ids, ISO `"O"` UTC timestamps, `ON DELETE CASCADE`.

**Comments explain why**, and name the failure mode being avoided. Cite the spec section when
the reason lives there. A comment restating the code is noise; a comment explaining why the
obvious approach is wrong is the most valuable thing in the file.

**C# style.** `sealed` on concrete classes, records with named-argument construction for domain
types, primary constructors, `ArgumentNullException.ThrowIfNull` guards at public entry points,
raw string literals (`"""`) for embedded SQL.

**Tests.** xUnit v2, plain `Assert.*`. No FluentAssertions, no Moq, no NSubstitute — hand-written
fakes as nested classes. Method names are snake_case sentences
(`Migration_is_idempotent`, `Deleting_a_thread_removes_its_messages_from_the_index`). Data tests
use a real SQLite **file** in a temp directory, never `:memory:`, because WAL, foreign keys and
cascade behaviour differ.

**Never call `SqliteConnection.ClearAllPools()`.** A test fixture has to release its pooled handles
before deleting its directory — on Windows the delete fails with a sharing violation otherwise —
but `ClearAllPools` is process-wide and xUnit runs test classes in parallel in one process, so it
disposes the `sqlite3` handle of a connection another test is in the middle of using. That surfaced
as an `ObjectDisposedException` thrown from somewhere unrelated, roughly once every few full runs:
the kind of failure that gets re-run until it passes and never diagnosed. Use
`SqliteConnection.ClearPool(connection)`, which is scoped to one connection string.

**No archive data in the repository** (decisions.md D30). Nothing shaped like a chat export is
committed — not a `result.json`, not a `.qhf`, not a VK message page, however synthetic. A file in
the source tree that looks like an export is one careless copy away from being somebody's real
correspondence, which is the same thing P6 refuses to let into a log. Export *shapes* are built in
code by `Archive.Import.Synthetic` — one builder per format, `TelegramExportBuilder` through
`WhatsAppChatBuilder` — and written to a temp folder when a test runs; tests still
read real folders, because that is most of what an importer does. `NoArchiveDataInTheRepositoryTests`
enforces it. A real archive for local testing goes in `scratch/`, which is ignored.

UI tests live in `Archive.Ui.Tests`. Most are plain view-model tests with no window at all; the
few that need a real Application use `Headless.RunAsync`, which drives Avalonia's headless
session directly rather than pulling in the xUnit v3 adapter (decisions.md D14). Use its async
overload when the body awaits a view model — blocking the headless UI thread deadlocks.

---

## After a change

- Schema or architecture changed? Update `AGENTS.md` and `README.md`.
- Departed from the spec? Add an entry to `docs/decisions.md`. Never edit the spec.
- `dotnet build` and `dotnet test` green before you call it done.
