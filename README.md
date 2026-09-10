| M7 | Packaging for Windows, Linux and macOS | ✅ || Source | What to point at | How well it is known |
|---|---|---|
| **Telegram** | the folder containing `result.json` | Documented format, verified against real exports |
| **Google Hangouts** | the Takeout folder containing `Hangouts.json` | Well known, and frozen — Hangouts shut down in 2022 |
| **VKontakte** | the folder containing `messages` | Structure confirmed against an existing parser; VK's markup has changed over the years |
| **QIP / QIP Infium** | a `History` folder of `.qhf` files | A closed binary format, read from a community reverse engineering |
| WhatsApp, Meta (Facebook/Instagram) | — | Planned |
| Signal, iMessage, Discord | — | Under consideration — local databases, or need third-party tooling |

Just point the app at the folder: it works out which format it is, says so, and refuses rather
than guessing if it does not recognize it.

**Only the Telegram reader has met a real archive.** The other three are built to the formats as
documented and covered by tests, which proves they do what was intended — not that what was
intended matches what is on your disk. They are deliberately strict: anything a reader does not
understand stops the import and names it, because for an archive a reader that silently mangles a
third of your messages is far worse than one that stops.

Export from Telegram Desktop as **JSON**, not HTML, and keep the whole export folder together —
the media files are referenced by relative path.# ahistory

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

**0.1.0.** The archive itself: schema, four importers, media store, per-person conversation view,
keyword search, and standalone builds for all three desktop platforms.

| Milestone | | |
|---|---|---|
| M0 | Skeleton — solution, build settings, project graph | ✅ |
| M1 | Storage — pragmas, migration runner, schema, EF mapping | ✅ |
| M2 | Content-addressed media store | ✅ |
| M3 | Telegram importer, proven idempotent | ✅ |
| M4 | Desktop shell, import UI, identity merging | ✅ |
| M5 | The continuous per-person conversation | ✅ |
| M6 | Full-text search | ✅ |
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

| Source | What to point at | How well the format is known |
|---|---|---|
| **Telegram** | the folder containing `result.json` | Documented, and verified against real exports |
| **Google Hangouts** | the Takeout folder containing `Hangouts.json` | Well known, and frozen — Hangouts shut down in 2022 |
| **VKontakte** | the folder containing `messages` | Structure confirmed against an existing parser; VK's markup has changed over the years |
| **QIP / QIP Infium** | a `History` folder of `.qhf` files | A closed binary format, read from a community reverse engineering |
| WhatsApp, Meta (Facebook/Instagram) | — | Planned |
| Signal, iMessage, Discord | — | Under consideration: local databases, or need third-party tooling |

Point the app at the folder and it works out which format it is, says so, and refuses rather than
guessing if it does not recognize it.

**Only the Telegram reader has met a real archive.** The other three are built to the formats as
documented and covered by tests, which proves they do what was intended — not that what was
intended matches what is on your disk. They are deliberately strict: anything a reader does not
understand stops the import and names it, because for an archive a reader that silently mangles a
third of your messages is far worse than one that stops.

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

Run the app:

```bash
dotnet run --project src/Archive.Desktop
```

It opens a save under your local application data by default; pass `--save <path>` for a
specific one. Or work headlessly:

```bash
dotnet run --project src/Archive.Cli -- init ~/archives/mine.db
dotnet run --project src/Archive.Cli -- import ~/archives/mine.db ~/Downloads/Telegram
dotnet run --project src/Archive.Cli -- sources ~/archives/mine.db
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

## Try it without an export

If you have not exported your own archive yet, generate one that contains no real data:

```bash
dotnet run --project src/Archive.Cli -- synth ~/synthetic --messages 200000
dotnet run --project src/Archive.Cli -- import ~/archives/demo.db ~/synthetic
dotnet run --project src/Archive.Desktop -- --save ~/archives/demo.db
```

It reproduces what makes real archives awkward — bursts separated by months of silence, a long
tail of two-word messages, Cyrillic alongside Latin, colliding timestamps, one sticker repeated
everywhere. It cannot substitute for a real export when it comes to *parsing*, since it only
produces shapes the importer already understands.

`ahistory stats <save.db>` reports what an archive is made of and what it costs.

## Logs

Logs go to `ahistory/logs` under your local application data — deliberately not beside the save,
which is meant to be copied around as a unit. They roll daily and are kept for two weeks.

**A log is safe to attach to a bug report.** It records counts, identifiers, durations and error
types; never message text, never chat or contact names. Things are referred to by id, so
`telegram:100` tells a diagnostic everything and a stranger nothing. The one exception is the
export folder path, recorded once per import, because you chose it and an import that cannot say
where it read from is hard to diagnose. There is a test that runs a real import and fails if any
of the fixture's own words reach the log.

Pass `--verbose` to either the app or the CLI for debug-level detail.

## Releases

Pushing a `release/x.y.z` branch builds standalone packages for Windows, Linux and macOS and
attaches them to a **draft** GitHub release. Publishing that draft is what creates the tag — a
branch keeps moving, and "what shipped as 0.1.0" should be answered by something that does not.

Builds are self-contained and untrimmed (EF Core reflection does not survive trimming), around
115 MB per platform, and **unsigned**: Windows SmartScreen will warn and macOS will refuse to open
them until allowed in System Settings.

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
