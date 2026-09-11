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

**0.1.0.** The archive itself: schema, thirteen importers, media store, per-person conversation view,
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
| M7 | Packaging for Windows, Linux and macOS | ✅ |

The AI layer, off until you switch it on. Point it at a local or hosted model and the app checks it
can call tools. A background runner splits the archive into sessions on gaps of silence and, once
you have agreed to where text goes, has the model:

- read each conversation worth reading and record what it says about the people in it, every fact
  citing its messages, shown beside the conversation and correctable there;
- merge the same fact said a dozen ways into one, and close values that changed;
- write a diary — a month at a time per person, then years and a portrait — where every sentence
  opens the message it rests on and months of silence are said out loud;
- index conversations for search by meaning, mixed with keyword search and labelled by which found what;
- read the text out of screenshots and transcribe voice messages, if you name models for those.

People, conversations or the whole archive can be left out, a daily token budget caps spending, and
everything the AI produced can be forgotten in one typed confirmation. The plan and what was built
are in [docs/ai-plan.md](docs/ai-plan.md).

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
The group itself is readable as a group on the Threads page: who was in it, whether or not they
ever spoke, and who said what.

**Merging is suggested, never automatic.** The app pairs up accounts carrying the same name across
platforms and offers them; you decide. It errs towards missing a pair rather than proposing a wrong
one: an under-merge is one click to fix, and an over-merge makes everything one person said into
something another person said.

## Supported sources

| Source | What to point at | How well the format is known |
|---|---|---|
| **Telegram** | the folder containing `result.json` | Documented, and verified against real exports |
| **Google Hangouts** | the Takeout folder containing `Hangouts.json` | Well known, and frozen — Hangouts shut down in 2022 |
| **Google Chat** | the Takeout folder containing `Google Chat`, or the Takeout root | Built to Takeout's layout; not yet run against a real export |
| **Skype** | the unpacked `.tar` from "Export files and chat history", containing `messages.json` | Built to the export as documented; real message ids, but shared files are linked rather than included |
| **VKontakte** | the folder containing `messages` | Structure confirmed against an existing parser; VK's markup has changed over the years |
| **QIP / QIP Infium** | a `History` folder of `.qhf` files | A closed binary format; reverse engineered, then corrected against real files |
| **Google Voice** | the Takeout folder containing `Voice/Calls`, or the Takeout root | Built to Takeout's markup; texts, calls and voicemail transcripts, keyed by phone number |
| **Facebook Messenger**, **Instagram** | the unpacked "download your information" folder, in JSON | Built to the documented layout; no message or account ids, so people are matched by name |
| **SMS and MMS** (Android) | the folder holding the `sms-….xml` from SMS Backup & Restore | Built to the app's documented format; people are keyed by phone number |
| **WhatsApp** | the folder holding the exported chat `.txt` files, unzipped, with their media | Built to the export as documented; **not yet run against a real export**, and the format varies by phone and locale |
| **Discord** | the unpacked data package, or a folder of DiscordChatExporter `.json` files | Built to both documented shapes. **The package holds only your own messages**; DiscordChatExporter holds everyone's |
| **Slack** | the unpacked workspace export, containing `users.json` and `channels.json` | Built to the documented export; workspace history, and it does not say which account is yours |
| Signal, iMessage | — | Under consideration: encrypted or device-local databases, which need a decrypt step outside the app |

Point the app at the folder and it works out which format it is, says so, and refuses rather than
guessing if it does not recognize it.

**Telegram and QIP have met real archives.** Every other reader is built to its format as
documented and covered by tests, which proves it does what was intended — not that what was
intended matches what is on your disk. All of them are deliberately strict: anything a reader does not
understand stops the import and names it, because for an archive a reader that silently mangles a
third of your messages is far worse than one that stops.

The QIP reader is worth a word of warning about the other two. It passed its tests and could not
open a single real file, because its fixtures were written from the same misreading of the format
as the reader itself ([D22](docs/decisions.md)). Fixtures keep a confirmed format from drifting;
they cannot confirm one.

Export from Telegram Desktop as **JSON**, not HTML, and keep the whole export folder together —
the media files are referenced by relative path.

**Formats that do not say who you are will ask.** VK's archive never names its account, and a QIP
`.qhf` names only the contact — so the app tells you it does not know, offers whatever it found,
and takes your answer. Leave it blank and your own messages still land on the right side of every
conversation, but "me" becomes a placeholder that will not line up with you on any other platform;
it shows up under "identified by name only" on the People page, where you can attribute it later.

For QIP, keep the folder QIP itself used: `<your own UIN>/History/<contact>.qhf`. That numeric
folder above `History` is the only thing in an export that says which UIN is yours. A history file
named after your own UIN is not a contact — it is messages you sent yourself, or authorization
events — and it is imported as **Saved messages**, the same as Telegram's.

`.ahf` files are QIP's archived history. This app does not read them: their layout has never been
confirmed against real files, and guessing at one is how the `.qhf` reader started out wrong. A
folder of them is recognized and refused by name rather than being called unreadable, and a folder
that mixes the two imports the `.qhf` files and tells you what it skipped.

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

## Opening a save made by an older version

A new version can need to change the shape of the database. When it does, the save is not migrated
out from under you — the app says what would change and asks, and the desktop app asks the same
question in place of its main window. In the CLI:

```bash
dotnet run --project src/Archive.Cli -- init ~/archives/mine.db --upgrade
```

A copy of the save is written beside it first (`mine.db.pre-004`) unless you pass `--no-backup`.
The copy is a complete database in its own right, and it is what you keep if you ever want the save
as it was, because **the schema only moves forward — there is no downgrade**. Your messages and
media are not touched; only the structure around them. Media is not copied, because migrations
never touch it.

A save made by a *newer* ahistory than the one you are running is refused rather than migrated, and
says to update the app. Do not start a new save in that situation — the existing one is the newer
of the two.

## Layout

```
src/
  Archive.Core      domain records and options contracts. Depends on nothing.
  Archive.Data      connections, pragmas, migrations, EF mapping, raw-SQL queries
  Archive.Media     content-addressed blob store
  Archive.Import    one reader per platform, normalizer, committer, export builders
  Archive.Ai        optional: providers, settings, and the model calls made against them
  Archive.Logging   logging setup shared by both heads
  Archive.Cli       headless init/import — how the importers are proven without a UI
  Archive.Ui        Avalonia views and view models
  Archive.Desktop   the thin desktop head
```

**No chat data is committed here.** Export shapes are built in code by `Archive.Import.Synthetic`
and written to a temporary folder when a test runs, because a file in the source tree that looks
like somebody's correspondence is one careless copy away from being somebody's correspondence. A
test enforces it; a real archive for local testing goes in `scratch/`, which is ignored.

`Core` depends on nothing, everything depends on `Core`, the heads depend on everything —
enforced by `SolutionLayoutTests` rather than by convention. Shared build settings live in
`Directory.Build.props` and are not repeated in any csproj, also enforced by a test.

## Configuration

Settings resolve from `appsettings.json` beside the executable, then a per-user config directory,
then environment variables prefixed `AHISTORY_` with `__` for nesting — for example
`AHISTORY_Archive__DatabasePath`. Invalid configuration throws at startup rather than surfacing
later as a broken window.

AI settings live apart from all of that, in `ahistory/ai.json` beside the logs — one configuration
per machine, shared by every save, and overridable with `AHISTORY_Ai__*`. They are deliberately not
inside the archive: a save is meant to be copied between machines and sometimes handed to someone,
and an API key inside it would travel with the correspondence. Nothing in the environment can
switch AI on; that is a question the app asks once, in the window.

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

`ahistory ai <save.db>` reports how much of it the AI layer has read. `--segment` splits it into
sessions first — no model, no key and no network, which is what makes the queue and its coverage
counts demonstrable before anything costs a token. `--extract` reads sessions with the configured
model, and `--all` runs everything the app would: reading, merging, the diary, the search index and
media. Both refuse to send anything until the endpoint has been agreed to, in the app or with
`--consent`, and `--limit N` stops after N jobs.

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

Pushing a `release/x.y.z` branch builds standalone packages for Windows, Linux and macOS,
publishes a GitHub release named after the version, and tags it `vx.y.z`. A version with a suffix
— `0.2.0-beta.1` — is marked a pre-release automatically. Pushing to `main` never releases.

The tag is what answers "what shipped as 0.1.0", because a branch keeps moving. Nothing is
packaged until the tests pass on all three platforms, so a release that exists was green.

Builds are self-contained and untrimmed (EF Core reflection does not survive trimming), around
115 MB per platform, and **unsigned**: Windows SmartScreen will warn and macOS will refuse to open
them until allowed in System Settings.

## Documentation

- **[message-archive-design.md](message-archive-design.md)** — the authoritative design spec.
- **[docs/decisions.md](docs/decisions.md)** — every departure from that spec, with reasoning.
- **[AGENTS.md](AGENTS.md)** — principles and conventions for anyone (or anything) writing code here.
- **[docs/v1-plan.md](docs/v1-plan.md)** — the V1 implementation plan as originally approved, kept as a historical record.
- **[docs/ai-plan.md](docs/ai-plan.md)** — the AI layer: configuration, the tool set, prompts, coverage, and the order the work happens in.
- **[docs/importer-roadmap.md](docs/importer-roadmap.md)** — what adding more platforms would take, and what the importer contract cannot yet express. Analysis, not commitments.

## Privacy

This is about as sensitive as personal data gets: your correspondence, and everyone who wrote to
you. There is no telemetry and no cloud component, and with AI switched off — which is how it
ships — nothing leaves your machine at all.

Switching AI on means choosing an endpoint, and that choice is what decides whether anything is
sent anywhere: point it at a model running on your own machine and it still never leaves. The app
says so where the choice is made rather than in a policy document, and the settings that hold your
API key are stored outside the archive, so a save you copy or hand to someone carries no key.

Nothing is sent until you have been shown how much would go, roughly what it costs and where, and
agreed for that endpoint — and the question names everything the same yes covers: conversations,
their index, photos and voice messages. Photos and voice messages are sent only if you name a model
for them. Someone you leave out is left out of all of it, their messages and their files, and never
sent; a whole archive can say no for itself, in the file, so it holds on any machine.

If you are archiving correspondence that is not your own, the design has a
[deliberate position on that](message-archive-design.md) — a save records where it came from, and
the more invasive inferences are off by default.

## License

[MIT](LICENSE) © 2026 Kirill Chekanov
