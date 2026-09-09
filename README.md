# ahistory

A local-first personal message archive.

Import your chat takeouts, merge the same person across platforms, and read one continuous
conversation per contact — years of it, searchable, in one place. Everything stays on your
machine.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-lightgrey)

## It is a chat history app first

The archive works completely on its own. Import an export, browse it, search it — no models, no
downloads, no background processing required, ever.

Optional AI enrichment layers on top of that: transcribing voice notes, reading text out of
screenshots, semantic search, and a cited knowledge base built from what people actually told you
over the years. All of it is additive. None of it is a precondition for reading your own history,
and the archive stays fully usable while that work runs in the background. This is
[principle P1](AGENTS.md) and it is enforced by a test, not by good intentions.

## Status

Early. V1 covers the archive itself: schema, Telegram importer, media store, per-person
conversation view, keyword search.

| Milestone | | |
|---|---|---|
| M0 | Skeleton — solution, build settings, project graph | ✅ |
| M1 | Storage — pragmas, migration runner, schema, EF mapping | ✅ |
| M2 | Content-addressed media store | ✅ |
| M3 | Telegram importer, proven idempotent | ✅ |
| M4 | Desktop shell, import UI, identity merging | ⬜ |
| M5 | The continuous per-person conversation | ⬜ |
| M6 | Full-text search | ⬜ |
| M7 | Packaging for Windows, Linux and macOS | ⬜ |

Later: transcription and OCR, embeddings and hybrid search, session extraction, the knowledge
base and diary. The V1 schema already carries the seams those need, so they arrive as new code
rather than as a migration of everything.

## How it works

**A save is self-contained** — a `.db` file plus a media folder beside it. Copy those two and
you have moved the archive. Nothing in the database stores an absolute path; media is addressed
by content hash, so the same save opens on any platform.

**Platform identity is separate from human identity.** A `Person` is the human; an `Identity` is
one account on one platform. Merging contacts repoints identities at a person — messages are
never rewritten, so unmerging is just as easy, which matters because automatic matching on names
and phone numbers gets it wrong.

**Nothing imported is ever overwritten.** Re-importing the same export changes nothing. A message
whose text changed between exports gains a revision rather than losing its original. The raw
export JSON is kept per message, so a parser gap is fixed by re-running rather than by asking you
for a new export.

**Group messages are stored once.** A contact's view is their DM thread unioned with the group
messages they sent — with surrounding context, because an isolated group line reads as nonsense.

## Supported sources

| Source | Status |
|---|---|
| Telegram (JSON export from Telegram Desktop) | Working |
| WhatsApp, Meta (Facebook/Instagram) | Planned |
| Signal, iMessage, Discord | Under consideration — these are local databases or need third-party tooling |

Export from Telegram Desktop as **JSON**, not HTML, and keep the whole export folder together —
the media files are referenced by relative path.

## Build and run

Requires the .NET 10 SDK.

```bash
git clone https://github.com/jncchds/ahistory.git
cd ahistory
dotnet build Ahistory.slnx
dotnet test Ahistory.slnx
```

Create a save:

```bash
dotnet run --project src/Archive.Cli -- init ~/archives/mine.db
```

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

`Core` depends on nothing, everything depends on `Core`, the heads depend on everything —
enforced by `SolutionLayoutTests` rather than by convention. Shared build settings live in
`Directory.Build.props` and are not repeated in any csproj, also enforced by a test.

## Configuration

Settings resolve from `appsettings.json` beside the executable, then a per-user config directory,
then environment variables prefixed `AHISTORY_` with `__` for nesting — for example
`AHISTORY_Archive__DatabasePath`. Invalid configuration throws at startup rather than surfacing
later as a broken window.

## Documentation

- **[message-archive-design.md](message-archive-design.md)** — the authoritative design spec.
- **[docs/decisions.md](docs/decisions.md)** — every departure from that spec, with reasoning.
- **[AGENTS.md](AGENTS.md)** — principles and conventions for anyone (or anything) writing code here.
- **[docs/v1-plan.md](docs/v1-plan.md)** — the V1 implementation plan as originally approved, kept as a historical record.

## Privacy

This is about as sensitive as personal data gets: your correspondence, and everyone who wrote to
you. It never leaves your machine. There is no telemetry and no cloud component. When AI features
arrive, a local model is the default and any hosted API is strictly opt-in.

If you are archiving correspondence that is not your own, the design has a
[deliberate position on that](message-archive-design.md) — a save records where it came from, and
the more invasive inferences are off by default.

## License

[MIT](LICENSE) © 2026 Kirill Chekanov
