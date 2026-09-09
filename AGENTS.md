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

- **No AI dependency in the app's spine.** `Archive.Core`, `Archive.Data`, `Archive.Import`,
  `Archive.Ui` and `Archive.Desktop` must not reference any inference, embedding or model-runtime
  package. AI belongs in its own projects, which the rest of the app must build and run without.
  Enforced by `SolutionLayoutTests.No_core_project_takes_an_ai_dependency`.
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
becoming a second one. This is what makes "me" definite for the knowledge base, and it is what
keeps message uids unambiguous — Telegram private-chat ids are relative to whoever exported
them, so two different people's archives in one save would collide. An archive someone gave you
belongs in its own save (§9, decisions.md D13).

**P6 — The save is portable, the app is not.** A `.db` plus its media folder must open on
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

---

## After a change

- Schema or architecture changed? Update `AGENTS.md` and `README.md`.
- Departed from the spec? Add an entry to `docs/decisions.md`. Never edit the spec.
- `dotnet build` and `dotnet test` green before you call it done.
