# ahistory

A local-first personal message archive. Imports chat takeouts, merges platform identities into
human persons, and presents one continuous conversation per person with full-text search.

Everything stays on your machine. A **save** is self-contained: a `.db` file plus a media
folder beside it.

- **[message-archive-design.md](message-archive-design.md)** — the authoritative spec. Code
  comments cite it by section (`§2`, `§6.4`).
- **[docs/decisions.md](docs/decisions.md)** — every departure from the spec, with reasoning.

## Status

V1 covers build-order steps 1–3: schema, Telegram importer, media store, per-person thread
view, FTS5 search. No transcription, embeddings or LLM layer yet — but the schema carries the
seams those need, so they arrive as new code rather than as a migration of everything.

| Milestone | | |
|---|---|---|
| M0 | Skeleton — solution, build settings, project graph | ✅ |
| M1 | Storage — pragmas, migration runner, schema, EF mapping | ⬜ |
| M2 | Content-addressed media store | ⬜ |
| M3 | Telegram importer, proven idempotent | ⬜ |
| M4 | Desktop shell, import UI, identity merging | ⬜ |
| M5 | The continuous per-person conversation | ⬜ |
| M6 | FTS5 search | ⬜ |
| M7 | Packaging for Windows/Linux/macOS, hardening | ⬜ |

## Layout

```
src/
  Archive.Core      domain records and options contracts. Depends on nothing.
  Archive.Data      connections, pragmas, migrations, EF mapping, raw-SQL queries
  Archive.Media     content-addressed blob store
  Archive.Import    Telegram reader, normalizer, committer
  Archive.Cli       headless init/import — how the importer is proven without a UI
  Archive.Ui        Avalonia views and view models
  Archive.Desktop   the thin desktop head
tests/
  fixtures/telegram golden export fixtures, one per parser trap
```

**Dependency rule:** `Core` depends on nothing, everything depends on `Core`, the heads depend
on everything. Enforced by `SolutionLayoutTests`, not just by convention.

Shared build settings (`net10.0`, nullable, warnings-as-errors, invariant globalization) live
in `Directory.Build.props` and are **not** repeated in any csproj — also enforced by a test.

## Build and test

```
dotnet build Ahistory.slnx
dotnet test Ahistory.slnx
```

Warnings are errors, so a warning breaks the build by design.

Perf tests are tagged and excluded from a normal run:

```
dotnet test Ahistory.slnx --filter Category!=Perf
```

## Configuration

Settings resolve from `appsettings.json` beside the executable, then a per-user config
directory, then environment variables prefixed `AHISTORY_` with `__` for nesting — e.g.
`AHISTORY_Archive__DatabasePath`. Invalid configuration throws at startup rather than
surfacing as a broken window.
