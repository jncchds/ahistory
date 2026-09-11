> **Forward-looking, not a record.** A plan for getting history from the platforms directly —
> on demand, and live where a platform allows it — rather than only from exports the user
> requested and unpacked. Written 2026-09-11 against thirteen readers, two of which have met a
> real archive. Nothing here is decided. It reconsiders one line of
> [importer-roadmap.md](importer-roadmap.md) — "the app imports exports, not devices" — and when
> any of it is built, that reversal and the choices under it belong in [decisions.md](decisions.md).

# Getting the data ourselves

## Why now

**Exports go stale the day they are made.** A Takeout takes days to arrive, a Telegram export is a
manual chore, and nobody re-exports every month. The archive is a snapshot of whenever someone last
bothered, and the most recent year — the one people actually search — is the one most likely to be
missing.

**Eleven of thirteen readers have never met real data** ([D22](decisions.md), [D33](decisions.md)).
A connector reading the user's own account is real data on demand. It also gives us a check that
fixtures cannot: the same messages reach the archive by two independent routes, and the two routes
have to agree. A builder written from a misreading agrees with a reader written from the same
misreading. An API response and an export file written by different teams at the platform do not
share our misreadings.

## Four routes, cheapest first

| Route | What it is | Network | Who it works for |
|---|---|---|---|
| **A. Watched folder** | A scheduled export lands in a folder; the app re-imports it when it changes | none | Every existing reader, unchanged |
| **B. Official API, your own credentials** | The app signs in as you and reads your history | to the platform | Telegram, Slack |
| **C. Local client database** | Read what a desktop client on this machine already synced | none | iMessage, Signal Desktop, legacy Skype |
| **D. Unofficial protocol client** | Pretend to be a linked device or a user client | to the platform | WhatsApp, Discord, Meta — **not recommended** |

### A. Watched folders — the free half of "live"

P3 already makes re-import idempotent and D12 already makes it cheap, so a folder that is re-read
whenever its contents change is live-ish for any format with scheduled exports:

- **SMS Backup & Restore** schedules backups to a local or synced folder — daily SMS, no new code
  beyond the watcher.
- **Google Takeout** can schedule an export every two months for a year.
- **DiscordChatExporter**'s CLI can be run on a schedule by the user.

What it needs: a list of watched folders per save (outside the save, like `ai.json`, since a path is
machine-specific — P7), a `FileSystemWatcher` plus a debounce long enough for a sync client to finish
writing, and the existing fingerprint to skip a folder whose file list has not changed. The
uid rule for derived-id formats (G3, occurrence counts per file) already makes overlapping backups
land on the same rows.

This is small, uses no network, and should come first whatever else is built.

### B. Official APIs

**Telegram** is the best case anywhere in this document, and it is the reference importer's
platform:

- **On demand:** the [Takeout API](https://core.telegram.org/api/takeout) is what Telegram Desktop's
  own export uses. A takeout session is confirmed by the user in another Telegram client, gets looser
  flood limits than ordinary calls, and walks history with `messages.getHistory`, a hundred messages
  per call.
- **Live:** an ordinary session receives updates while connected, and `updates.getDifference`
  catches up on whatever happened while the app was closed.
- **Library:** [WTelegramClient](https://wiz0u.github.io/WTelegramClient/) is MTProto written in
  managed C# with no native binaries, so it adds no per-RID payload (the property D32 protects),
  and it supports takeout sessions. TDLib is the alternative and is a native library per platform.
  Check its licence and pin a version before adopting it.
- **Needs an `api_id` / `api_hash`**, issued per application at my.telegram.org. Open question
  below.

**Slack** works with a user token from a Slack app the user creates in their own workspace:
`conversations.history` and `conversations.replies` for backfill, polling or Socket Mode for live.
The same caveat as the Slack reader applies: this is workspace history, and D33 already rates that
low for a personal archive. Build it only if someone asks.

**VK** needs the `messages` scope, which VK has been closing to third-party applications. It is
probably unreachable. Verify before planning anything.

### C. Local client databases

- **iMessage** — `~/Library/Messages/chat.db`, plain SQLite, macOS only, readable once the user
  grants Full Disk Access. On demand is one read; live means watching the file. The text of newer
  messages is in an `attributedBody` typedstream blob rather than the `text` column, and that
  decoder is the real work. No network at all. It is the richest source in the roadmap's table.
- **Signal Desktop** — Signal now offers to
  [transfer message history when linking a desktop](https://signal.org/blog/a-synchronized-start-for-linked-devices/)
  (all text; the last 45 days of media). A freshly linked Signal Desktop therefore holds the whole
  text history, in SQLCipher, with its key wrapped by the OS keystore. Reading it means a native
  SQLCipher build plus DPAPI, Keychain or libsecret, which is the native-dependency cost D32 chose
  against. Defer until someone asks, and then weigh it against documenting an external decrypt
  step, which is what the roadmap already recommends.
- **Legacy Skype `main.db`** — plain SQLite, on demand only (Skype is gone). Already on the
  roadmap as a file reader, so it does not belong here.
- **Telegram Desktop's `tdata`** is encrypted and undocumented. Route B covers Telegram.

### D. Unofficial clients — why not

- **WhatsApp** via whatsmeow or Baileys would work technically: a linked companion device receives
  a history sync blob. But WhatsApp prohibits unofficial clients, and whatsmeow
  [issue #810](https://github.com/tulir/whatsmeow/issues/810) documents warnings and bans landing
  on low-volume, otherwise legitimate use. Losing a WhatsApp account costs someone far more than
  their archive gains. Keep the export reader.
- **Discord** DMs need a user token. Automating a user account is against Discord's terms and puts
  the account at risk. DiscordChatExporter already exists, the user runs it at their own risk, and
  route A picks up its output.
- **Messenger, Instagram** have no personal messaging API. Exports only.

## Per platform

| Platform | On demand | Live | Route | Verdict |
|---|---|---|---|---|
| Telegram | takeout session | updates + getDifference | B | **Build** — first connector |
| iMessage | read `chat.db` | watch `chat.db` | C | **Build** — second, macOS only |
| SMS (Android) | scheduled backup | scheduled backup | A | Free with watched folders |
| Google Chat / Voice / Hangouts | scheduled Takeout | — | A | Free with watched folders |
| Discord | DiscordChatExporter, user-run | — | A | Free with watched folders |
| Slack | user token | poll / Socket Mode | B | Only if asked |
| Signal | linked Desktop DB | watch DB | C | Deferred — native SQLCipher |
| WhatsApp | — | — | D | **No** — ban risk |
| Messenger, Instagram | — | — | — | No API |
| VK | `messages` scope | long poll | B | Probably closed — verify |
| Skype, Hangouts, QIP | — | — | — | Dead platforms: exports only |

## What the codebase has to change

### The contract survives; the connector is a new kind of importer

A connector pushes into the same `IImportSink` and produces the same `NormalizedMessage`, so
dedupe, revisions, media, sources and the schema downstream are untouched, the same way D20 held
for every file reader. It is a separate interface rather than a new `IPlatformImporter`, because
it has no folder to `Detect` and it is resumable where `Read` is one pass:

```csharp
public interface IPlatformConnector
{
    string Platform { get; }                      // the same id as the file reader: "telegram"
    Task BackfillAsync(IImportSink sink, ISyncState state, CancellationToken ct);
    Task FollowAsync(IImportSink sink, ISyncState state, CancellationToken ct);  // null for on-demand-only
}
```

### Same message, same uid, same hash, whichever route it came by

This is the requirement that decides whether any of this works. A message that arrives from the
API and later from an export, or the other way round, must produce **the same uid and the same
`ContentHash`**. Otherwise every message is stored twice, or every message becomes a spurious
revision. Both are silent, and both are exactly the "looks right, is wrong" failure D20 exists to
refuse.

For Telegram the robust way to get this is **one normalizer**. The connector translates a TL
message into the export's JSON shape (`text_entities`, `date_unixtime`, `from_id` with its prefix)
and hands that to `TelegramNormalizer`. Two normalizers would drift; one cannot. `RawJson` is then
replaced with the TL object's own serialization, so §1's "keep the original" means what came off
the wire.

Two things only a real account can confirm, and they are why the first Telegram milestone ends with
a cross-check rather than a UI:

- **Chat ids.** The uid is `tg/<chat_id>/<message_id>`. MTProto's channel id and the Bot API's
  `-100…` form differ, and which one Telegram Desktop writes into `result.json` for a supergroup
  has to be read off a real export, not assumed.
- **Entities.** Whether the export's `text_entities` split and TL's offset-based entities convert
  to byte-identical arrays for mentions, custom emoji and nested formatting.

`ahistory sync-check <save> <telegram-export>` fetches the chats present in an export through the
API and reports every uid present on one side only and every uid whose hash differs. Zero on both
counts against a real export is the exit criterion. It is also the first time since QIP that a
reader is checked against something it was not written from.

### The committer must not hold a transaction across the network

`ImportCommitter` begins a transaction in its constructor and holds it until a batch of 1,000 is
full. For a file that takes milliseconds. For a connector the batch fills over minutes of network
calls and flood waits, and a write transaction held that long stalls every other writer in the app
and blocks WAL checkpointing. P1 forbids this for model calls, and the reason is identical.

- **Lazy begin.** Open the transaction on the first write of a batch, not on construction or after
  a commit.
- **The connector owns the batch boundary.** Fetch a page, then write the page in one short
  transaction and commit. Nothing is awaited while a transaction is open.
- **`Complete()` is too heavy per page.** FTS `optimize` and `ANALYZE` run once at the end of a
  backfill and on idle during live sync, not per batch.

### Runs, cursors and a crash

- **One `import` row per backfill, and one per live session**, closed when the app exits or every
  few hours. A run marked `running` for three weeks tells nobody anything. `source_path` is
  `telegram:api`; `source_fingerprint` is a hash of the cursor state at start. Both columns are
  `NOT NULL` today and need no change.
- **`010_sync.sql`** adds `sync_state (source_id, scope, cursor, updated_utc)`. Scope is a chat id or
  `*` for global update state (pts/qts/date). The cursor is written in the **same transaction as
  the page it describes**, so a crash can never move the cursor past messages that were not
  committed.
- A run left `running` by a crash is closed as failed at startup. Files have the same gap today and
  it has not mattered, but a run that happens on every launch will make it matter.

### Edits and deletions

**Edits** are where live sync beats exports: an edit event carries the message id, so it becomes a
`message_revision` through the existing path, including for the in-between versions an export never
sees.

**Deletions** must not delete (P2). Keeping what was said is the archive's purpose. The open
question is whether to record "deleted on the platform at T". Recording it is useful and costs one
small table. Ignoring it is simpler. Either way the message stays.

### Media

A connector downloads straight into the media store through the existing lazy callback, so a
message that is already stored costs no download, the same way it costs no re-hash (D12). Backfill
is text first, then media as a second pass, so a ten-year chat is readable in minutes. The policy
is a setting: photos and voice by default, video and files over a size cap on request.

## Credentials, consent, logs

**A Telegram session is the account.** Whoever has the session file can read and send as the
user. It is a far worse thing to leak than an AI key, so the `ai.json` precedent is necessary but
not sufficient:

- Stored outside the save, under `ahistory/connectors/`, never in the database (P7, and the
  README's promise that a copied save carries no key).
- Encrypted at rest with DPAPI on Windows (`ProtectedData`, managed). On macOS and Linux the
  keystore needs interop; until it exists, the file is `0600` and the settings page says so plainly.
- **Disconnect** logs the session out on Telegram's side too, not just deletes the file.

**Off until switched on, and no trace when off.** Today the README says that with AI off nothing
leaves the machine. A connector talks to the platform. It sends nothing from the archive, but it
is still a network connection, so it gets P1's treatment: off by default, per connector, and when off
there is no connection, no process and no rail entry. A test in the style of `AiPageTests` enforces
it, and the README's privacy section is rewritten to say exactly what is contacted and when.

**No daemon.** Like `AiRunner`, sync lives and dies with the app: "live" means while the app is
open, and `getDifference` catches up on launch. Syncing a closed app would be a promise about the
machine, and the AI plan declined to make that promise for the same reason.

**Logs (P6).** Counts, chat and message ids, flood-wait durations, error types. Never the phone
number, username, message text, session or token. `LoggingPrivacyTests` gains a fake connector,
and it logs the auth flow too, because a login is where a phone number most naturally slips into a
debug line.

## Layering

A new project, `Archive.Sync`, beside `Archive.Ai`: it references Core, Data, Import and Media;
`Archive.Ui`, the CLI and the desktop head reference it. `No_storage_project_references_the_sync_project`
joins the AI layout tests, so a network client never becomes a dependency of the storage layer.
Every route-C reader that needs no network (iMessage) could live in `Archive.Import` as a file
reader. It would only need the watcher from route A for its live half.

## Testing under D30

- The transport sits behind an interface; the TL→export-shape translation is tested with TL
  objects built in code, like the export builders. Nothing that looks like account data is
  committed.
- A fake connector proves the plumbing: cursor and page share one transaction, a cancelled backfill
  resumes, nothing is held open across an `await`.
- `sync-check` against the user's own account and export in `scratch/` is the one test that
  confirms a format, and it is manual by nature.

## Order

1. **Watched folders.** Small, no network, and every existing reader becomes "live" as far as
   scheduled exports allow. Demonstrable on SMS backups.
2. **Sync plumbing, proven with a fake:** `Archive.Sync`, lazy-begin committer batches,
   `010_sync.sql`, the secret store, the switch and its no-trace test, the logging test.
3. **Telegram on demand through the CLI:** `ahistory connect telegram`, then
   `ahistory sync <save>`. Ends with `sync-check` agreeing with a real export, **before any UI**.
4. **Telegram in the desktop app:** sign-in flow, backfill progress, then live updates while open.
5. **iMessage** on macOS: `chat.db` reader with the typedstream decoder, and the watcher for live.
6. Slack, Signal Desktop and VK only on request, each re-verified first.

## Open questions

- **Telegram `api_id`:** user-supplied (like the AI endpoint: nothing tied to the maintainer, but a
  setup step that loses people), or one shipped with the app (common for open-source clients, and
  tied to the maintainer's Telegram account and its standing).
- **Deletions:** record "deleted on the platform at T", or ignore?
- **Default media policy** for a backfill: everything, or photos and voice with a size cap?
- **Whose chats:** backfill everything, or let the user pick chats, so channels they merely follow
  are not pulled into a personal archive? Picking seems right; channels are not correspondence.
