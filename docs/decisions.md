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

### Correction (M6): what prefix expansion actually buys

The original wording here implied prefix expansion recovers Slavic inflection. It does not, and
the claim was too strong.

Prefix expansion matches words **starting with** what was typed. A stem therefore finds every
form built on it — `Праг` finds `Прага`, `Праге`, `Прагу`. But a full inflected form finds only
itself: `Прага` does not match `Праге`, because they diverge at the final character. Reaching one
inflection from another is lemmatization, which FTS5 provides for no Slavic language, and which
trigram would not fix either — `Прага` is not a substring of `Праге`.

So the tokenizer choice stands, but for a smaller reason than first written: prefix expansion
helps someone typing part of a word, and stems work as search terms. The honest response to the
remaining gap is to say so in the UI rather than to let a user conclude their archive is empty.
`A_stem_reaches_every_form_built_on_it` pins the real behaviour in both directions.

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

---

## D14 — Avalonia 12, and headless tests without the xUnit adapter

The plan named Avalonia 11; the current release is **12.1.2**, and that is what the app targets.
Two consequences worth recording.

**`TextBox.Watermark` became `PlaceholderText`.** Trivial, except that Avalonia's XAML warnings
(`AVLN*`) are *not* covered by `TreatWarningsAsErrors` — they pass a build that reports "0
Warnings". A XAML deprecation or a binding the compiler dislikes will therefore slip through
unless someone reads the full build log. Worth a periodic `dotnet build -v n | grep AVLN`.

**The headless test adapter was rejected.** `Avalonia.Headless.XUnit` for Avalonia 12 depends on
xUnit **v3**, and the rest of the solution is on v2 (AGENTS.md). Referencing it produced
`CS0433: FactAttribute exists in both xunit.core and xunit.v3.core`. Rather than run two test
frameworks in one repository, `tests/Archive.Ui.Tests/Headless.cs` drives
`HeadlessUnitTestSession` directly — about fifteen lines, and the only thing the adapter would
have provided is an `[AvaloniaFact]` attribute.

That helper has an async overload for a specific reason: view models continue on the UI thread by
design, so blocking that thread inside a test body to wait for one deadlocks instantly — the
continuation needs the very thread the test is holding. The first version of these tests hung for
exactly that reason.

---

## D15 — The per-person union query, and what its plan can honestly promise

§4's per-person conversation is one index range scan per source — per direct thread, and per
identity for that person's group messages — merged with UNION ALL.

**Each arm is wrapped in a subquery.** SQLite rejects `ORDER BY` and `LIMIT` written directly
inside a UNION branch, and bounding each branch before the merge is the entire point: a single
outer WHERE and LIMIT over the union makes SQLite materialize every arm in full and then discard
almost all of it.

**The plan cannot promise "no temp b-tree", and the plan document was wrong to ask for it.**
SQLite does not merge pre-sorted UNION ALL arms, so the merged rows are sorted once. That sort is
real but bounded by *(arms × limit)* — a few hundred rows — rather than by the archive. What the
test does assert is that no arm degenerates into `SCAN message`, which is the failure that a
fixture would never reveal by timing and that would be ruinous at half a million rows.

Arm count is bounded by how many accounts a person has and how many direct threads they appear
in: a handful, not a function of archive size.

**Group context is fetched on expand, not with the page.** Most group lines are never expanded,
and fetching ±8 messages for every one of them would multiply each page load by the context
radius for content nobody asked to see.

---

## D16 — Logging: Serilog in the heads, abstractions everywhere else, and a privacy rule with teeth

**Layering.** `Archive.Data`, `Archive.Import`, `Archive.Media` and `Archive.Ui` reference only
`Microsoft.Extensions.Logging.Abstractions` and take an optional `ILogger`. The heads choose the
implementation. `Archive.Logging` exists because both heads need the identical configuration and
the alternative was duplicating it — it depends on Core alone, and the desktop head's dependency
on it is the one exception the "thin head" test now allows.

Loggers are optional parameters defaulting to `NullLogger`, so every existing call site and test
kept working and a missing registration degrades to silence rather than a startup crash.

**Serilog for the file sink**, rather than hand-rolling one. Rotation, retention, size limits and
concurrent writes are all easy to get subtly wrong, and none of that is this project's problem to
solve. It stays in the heads.

**Logs live under application data, never beside the save.** A save is meant to be copied and
moved as a unit (P7), and logs riding along with it is exactly how a diagnostic file ends up
somewhere nobody intended.

### The privacy rule is the point

This archive is people's private correspondence. A log that quotes it is as sensitive as the
archive itself, while being far more likely to be attached to a bug report or pasted into an
issue. So: **counts, identifiers, durations and error types — never content, never names.** Not
just message text; a list of who someone talks to is as revealing as what they said, so chat and
contact display names are out too. Things are referred to by id.

The one deliberate exception is the export folder path, logged once when an import starts: the
user chose it, and an import that cannot say where it read from is very hard to diagnose.

`LoggingPrivacyTests` runs a real import at Debug level through a capturing logger and asserts
that none of the fixture's own words — message text, chat names, contact names — appear anywhere
in the output. It is paired with a test that the log is still *useful*, because a privacy rule
that produces a silent app has failed differently.

The negative control was run deliberately: replacing `{ThreadId}` with `{ThreadName}` in one
Debug line makes the test fail. The failure mode being guarded against is not malice, it is
someone adding one helpful-looking chat name six months from now.

---

## D17 — Measured at 495k messages, and what it changed

A synthetic archive (`ahistory synth`) was generated and imported to see what the design actually
costs. Numbers on this machine, 495,616 messages across 120 chats, ten years, no media beyond one
repeated sticker:

| | |
|---|---|
| Import | 111.7s — **4,436 messages/sec** |
| Database | **645 MB** (1,365 bytes per message) |
| Media | 29,191 attachment references → **1 file on disk** |
| Conversation page | 1.0 ms first page, **0.9 ms two thousand messages deeper** |
| Thread page | 0.3 ms |
| Group context expansion | 0.4 ms |
| Search, rare word | 0.1 ms |
| Search, very common word | 130 ms |

**Keyset paging does what it was chosen for.** Reading deeper into a conversation costs the same
as reading the start. An `OFFSET` would have re-read everything it skipped, and at this size the
difference would be seconds.

**Media deduplication works exactly as §1 predicted** — twenty-nine thousand references, one file.

### Where the 645 MB was

`dbstat` is not compiled into this SQLite build, so each component was measured by dropping it on
a copy and vacuuming:

| | |
|---|---|
| `message.raw_json` | 206 MB |
| `message_source` | 121 MB |
| message indexes | 85 MB |
| `message.entities_json` | 76 MB |
| `search_document` | 68 MB |
| FTS5 index | 46 MB |
| `reaction` | 20 MB |

Two things came out of that.

**raw_json is now Brotli-compressed** (`RawJson`). It is repetitive JSON read only when the
importer is improved, so compression costs nothing that matters — and the import got *faster*,
104s against 112s, because less writing outweighs the compressing. The §1 guarantee is unchanged:
`RawJsonTests` asserts the original text comes back byte for byte.

The gain was smaller than hoped — 645 MB to **550 MB**, not the ~470 MB a 4x ratio would give —
because each payload is only a few hundred bytes and per-row compression cannot exploit the
redundancy *between* rows. A shared dictionary would do better and is not worth the complexity yet.

**`message_source.seen_utc` is gone.** It was an ISO timestamp per message duplicating the
`started_utc` of the run that `first_import_id` already points at. At one row per message a
redundant string is megabytes of nothing.

`--no-raw-json` now exists for anyone who would rather trade the guarantee for the space, and
`ahistory stats` reports the number so the choice can be made from evidence rather than guessed.

### What this cannot tell us

The generator only produces shapes the importer already understands, so it can never surface a
parsing trap nobody has thought of. Format compatibility still waits on a real export.

---

## D18 — Two follow-ups from D17's measurements

**The thread list: correlated subqueries, not a join and GROUP BY.** 63 ms to 22 ms at 495k
messages. The join reads every message row and then groups them; the subqueries are answered
entirely from `ix_message_thread_time` as a covering index, one bounded range per thread.

It is still proportional to the number of messages, because counting them is. The next step, if
22 ms is ever felt, is a maintained count on `thread` — but that costs an UPDATE per message
during import, which is a poor trade for a page that loads once on navigation and off the UI
thread.

**The result count is capped.** The search page ran two passes over the same matches: the search
itself, then a full count. On a very common word that was 130 ms plus 100 ms.

The first attempt — skipping the count when the results were not truncated — turned out to be
worthless. It helps only searches that were already fast, because the expensive case is exactly
the one where the results *are* truncated and the count is still needed. Measurements said so:
233 ms before, 233 ms after.

What works is capping it. `Count` stops at 1,000 and reports whether it was cut off, so the page
says "1,000+ matches" rather than paying a second full pass for a number nobody reads precisely.
Below the cap the count is exact and the page says so. **233 ms to 136 ms**, and what remains is
the search itself — bm25 across every match plus snippets for the ones shown — which is inherent.

---

## D19 — Telegram's layout, deliberately not Telegram's colours

The window follows Telegram Desktop: a narrow icon rail, a list of people, and the conversation
as bubbles. That is the shape people already read messages in, and inventing a different one
would cost familiarity to buy nothing.

The palette does not follow it. Telegram is cool blue-grey; this is warm — dark paper rather than
dark glass, amber where Telegram is blue. The app is an archive of things that already happened,
not a live chat, and it should not be mistaken for one at a glance.

Details that carry more than they look like they should:

- **The bubble's corner nearest its speaker is flattened.** That single detail is what makes a
  stack of rectangles read as speech, and what lets you see who said what without reading a name.
- **Avatar colour is derived from the name, not assigned.** The same person is the same colour
  every launch, which is what makes a list scannable without reading it. `string.GetHashCode` is
  randomized per process and would have given someone a new colour every time the app opened, so
  the hash is a plain character sum — a poor hash and exactly the right one.
- **Bubbles are labelled with the room, not the speaker.** In a view of one person every incoming
  message is from that same person, so their name on every bubble is a column of the same word.
  Which group it came from is the part that varies (§4).
- **Fetch order and reading order are opposite.** Pages come newest-first, because that is what
  keyset paging backwards from the present gives and what makes opening a ten-year conversation
  instant; they are inserted at the front so the conversation reads downward like a conversation
  rather than upward like a log.

The screenshots that verified this were rendered headlessly through Avalonia's Skia backend,
which is also how the layout bugs were found — the first render showed every contact named
"Someone", which was a defect in the synthetic generator rather than the UI: it wrote a literal
name on every message while varying only the id.

---

## D20 — Four platforms, one schema, and importers that refuse rather than guess

`IPlatformImporter` turns a format into `NormalizedMessage`; everything downstream — dedupe, uids,
media, sources, the schema — is untouched by adding a platform. §2's build order calls the second
importer the thing that proves the schema was right, and it held: no schema change was needed for
Hangouts, VK or QIP.

Which importer reads a folder is **detected**, not chosen from a dropdown. Someone with a decade of
archives has folders whose format they have long since forgotten, and asking them to classify it
correctly before anything works is asking the wrong person. Each importer answers for itself and
the most confident wins; when nothing recognizes a folder the app says what it looked for rather
than picking the least-wrong reader.

### The rule that makes this safe

**An importer refuses rather than guesses.** For an archive, a reader that looks like it worked and
silently mangles a third of the messages is far worse than one that stops and names the byte it did
not understand — the second can be fixed, the first is discovered years later. So every new reader
throws on anything it does not recognize: a Hangouts event with no id, a VK message with no
`data-id`, a QIP block whose signature is wrong or whose timestamp is implausible.

### What each reader actually rests on

| Format | Basis | Confidence |
|---|---|---|
| Telegram | Documented export, verified against real ones | High — the reference importer |
| Google Hangouts | Takeout JSON, a dead and therefore frozen format | Good — the shape is well known |
| VK | HTML, structure confirmed against [Darkar25/VkArchiveParser](https://github.com/Darkar25/VkArchiveParser) | Medium — VK's markup has changed across years |
| QIP / QIP Infium | `.qhf` binary, per [MolinRE/QIParser](https://github.com/MolinRE/QIParser) | Lowest — closed format, no specification, documented layout has unexplained gaps |

None of the three new readers has been run against a real archive. The fixtures are built to the
formats as documented, which proves the readers do what was intended — not that what was intended
matches what is on someone's disk. The strictness is what makes that gap safe to ship: a wrong
assumption stops the import instead of filling an archive.

### Details worth keeping

- **Hangouts timestamps are microseconds.** Read as milliseconds — the obvious assumption — the
  entire archive lands in January 1970.
- **A Hangouts LINE_BREAK segment carries no text**, so concatenating only the text fields runs
  paragraphs together.
- **The Hangouts owner is stated** in `self_conversation_state`. The first version inferred it from
  "who is in every conversation", which a test immediately showed is ambiguous in a
  two-conversation export.
- **VK omits the sender link on your own messages.** Read as "unknown", half of every conversation
  ends up on the wrong side.
- **VK dates are Russian text**, and this app runs with invariant globalization, where `ru-RU`
  collapses to the invariant culture and every one of them fails to parse. The month names are
  mapped explicitly — both `май` and `мая`.
- **QIP's text is obfuscated** with `b = 255 - b - i - 1`, which is its own inverse. Not encryption,
  and never was.
- **QIP names only the contact**, so the owner comes from the numeric folder above `History`.
  Without it every message renders as incoming and a conversation reads as a monologue.

## D21 — `WasNew` on a media put is exact for the importer, advisory under a race

The media store deduplicates by moving a staged file to its content address with
`File.Move(overwrite: false)` and catching the collision. That was chosen over checking `Exists`
first because the check races and can rewrite a file another caller is reading.

The move rejects an existing destination — but not identically on every platform, which cost a red
CI job to learn. A test asserted that exactly one of sixteen simultaneous puts of the same bytes
reports `WasNew`; that passed on Windows and Linux and failed on macOS with two of sixteen. .NET
does not implement `overwrite: false` the same way on every filesystem, and the check-then-rename
path it can take is racy by construction.

Nothing is corrupted by this, and the assertion was the wrong one. Two callers storing *identical
bytes* is precisely the case where losing the race is harmless: whichever file wins has the
contents the hash claims. So the test now asserts what actually holds everywhere — one address,
one file on disk, correct contents — and `WasNew` is documented as exact when one caller stores at
a time and advisory otherwise.

That is not a weakened guarantee for anything that depends on it. `WasNew` feeds the "stored /
deduplicated" counts on an import report, and an import commits media from one thread; the counts
it produces are exact. Had the flag been load-bearing for correctness rather than for a statistic,
the right response would have been to make the claim atomic instead of to describe it honestly.

**The general lesson, since it will recur:** a test that passes on the machine it was written on
can encode that machine's behaviour rather than a real guarantee. Three-OS CI is what tells the
difference, which is the whole reason [P7](../AGENTS.md) is enforced by running the suite on all
three rather than by intending to be portable.

## D22 — The QIP reader was wrong in three places, and its own tests agreed with it

0.1.0 shipped a QIP reader that could not open a single real `.qhf` file. Eleven QIP Infium
histories were the first ones it ever met, and it rejected all eleven on the first check.

Three faults, and the corrected layout is documented in full on `QipImporter`:

- **Both size fields count what follows them, not what they introduce.** The header's field is 8
  short of the file (it excludes `"QHF"`, the version byte and itself); a message block's is 6
  short of the block. Reading the header's as the file's own length declared every real export
  truncated, which is why nothing could be opened at all.
- **The message type is the byte at `+0x1C`**, the third byte of field 3, whose first byte is the
  direction. The reader classified on the int16 at `+0x06`, which is a field *id* and is 1 in every
  block of every file — so `"service"` was unreachable and the six authorization events in the
  corpus were filed as ordinary chat. Nothing failed; the archive was quietly wrong.
- **The blocks are id/length/value triples**, not fixed offsets. The offsets happen to be constant
  because every field has so far had a constant length. The reader now checks each id and length
  marker, so a block shaped differently stops the import instead of yielding a message assembled
  from fields that shifted underneath it.

**The part worth keeping:** the reader had six tests over fixtures built in code, and they passed.
They passed because the fixture builder was written from the same misreading as the reader — the
same author, the same afternoon, the same wrong document. Two things agreeing proves nothing when
one was derived from the other. The fixtures were not useless, but what they establish is narrower
than it looked: they keep a *confirmed* format from drifting; they cannot confirm one.

Both size fields being off by a constant is also the most benign possible version of this: it
failed loudly and immediately. Had the corpus been a little different, the same class of error in
the type field would have produced an archive that imported cleanly and was subtly wrong — which
is the failure this project is most concerned with, and the reason the strictness stayed and grew
rather than being relaxed to get the files in.

Three tests now pin what only real files could establish: a size field counting the whole file is
refused, authorization messages are service messages and ordinary ones are not, and a block with
different field markers is refused. The README no longer claims Telegram is the only reader that
has met a real archive.

## D23 — A save whose schema drifted is refused when it is opened

Importing into a save created a day earlier failed with `NOT NULL constraint failed:
message_source.seen_utc` — a column the schema no longer has. It had been dropped from
`001_core.sql` as redundant, and migrations are recorded by filename, so the edit reached every new
save and no existing one.

Editing an applied migration is the mistake; the numbered files are append-only in practice even
though nothing enforced it. But the failure mode is what needed fixing. It arrived as a SQLite
error about an unfamiliar column, thrown from inside an import, several frames deep — a stack
trace that says nothing about what is actually wrong or what to do about it.

`Migrate` now fingerprints the save's `sqlite_master` and compares it with a database built from
the migrations and nothing else, refusing the save if they differ. It catches drift whatever caused
it — an edited migration, a hand-altered table, a save from a newer build — and says so while the
save is opening, in a sentence, naming the only thing that fixes it: import into a new save.

A checksum column on `schema_migration` was the obvious alternative and is strictly worse here. It
could only verify migrations applied *after* it was introduced, so it would have been blind to the
save that prompted this, and it needs a schema change of its own to add.

The cost is three migrations against a scratch file each time a save is opened, which does not
register against a test suite that opens hundreds. The consequence to be aware of: the desktop head
treats a startup failure as fatal, so a drifted save now stops the app from launching rather than
letting it open and fail at the first import. That is the intended trade — a schema the code does
not recognize cannot be safely *read* either, and a save that silently answers queries from tables
that are not the ones the queries were written against is the outcome this whole file keeps
choosing against.

## D24 — Four states for a save's schema, and an upgrade nobody performs by accident

D23 added a check that a save's schema is the one the migrations produce, and refused it otherwise.
That was right about the case it was built for and wrong about every other one, because it had only
a single unhappy state. A save made by a *newer* build was told to start a new save — advice that,
followed, discards the newer of the two.

A save's `schema_migration` names now decide which of four states it is in, before anything is
applied:

| State | How it is recognized | What happens |
|---|---|---|
| Up to date | the same migrations, and the schema they produce | opens |
| **Behind** | its migrations are a leading run of this build's | the only upgradeable state |
| **Ahead** | it has migrations this build does not | refused: *update ahistory* |
| Diverged | all migrations run but the wrong schema, or a gap in the middle | refused: import into a new save |

The prefix test is sound only because migrations are append-only and run in filename order. A gap
in the middle is deliberately not an upgrade: a save missing `003` while holding `004` was not left
behind by an older build, and running `003` over the top of it would produce something no version
of this app has ever made.

**Upgrading is never a side effect of opening a save.** It is one-way — there is no rollback and
there will not be one — so `Migrate` throws `SchemaUpgradeRequiredException` for a save that is
behind, and the decision reaches whoever can ask. The CLI turns it into `init <save> --upgrade` and
prints the migrations by name; the desktop head shows the question instead of the main window,
because there is nothing to browse until the save is readable and a dialog over an empty archive
reads as an error rather than a question.

The copy taken first is `VACUUM INTO` rather than a file copy: it yields one consistent file with
the WAL folded in, where copying `archive.db` beside a live `-wal` yields a file missing whatever
had not been checkpointed. It refuses to overwrite, because the file it would overwrite may be the
only copy of a save nobody can rebuild. Only the database is copied — migrations never touch media,
which is content-addressed in a directory of its own.

**The runtime check is the backstop, not the fix.** What actually went wrong in D23 was a migration
edited after it had shipped, and no amount of classification prevents that — it only reports it,
late, on someone else's machine. `MigrationTests` now pins the SHA-256 of every migration that has
shipped, so editing one fails a test on the commit that does it. That is ~20 lines and would have
prevented the whole episode. Note the failure mode of the guard itself: updating a hash to make the
test pass is the same mistake with an extra step, which is why the table says so in the file.

A checksum column on `schema_migration` was the obvious alternative and is worse. It could only
verify migrations applied after it was introduced, so it would have been blind to the save that
prompted D23; it needs a schema change to add; and it would change `schema_migration`'s own
definition, which is *inside* the fingerprint — so introducing it would classify every existing
save as diverged.

`004_provenance.sql` records which build created a save and which last changed its shape, so
"made by a newer version of ahistory" can become "made by ahistory 0.3.0; this is 0.2.0". Written
by the app rather than by the migration, which has no way to know what is running it, and only when
the schema changes — a save that is merely read is not written to. `created_by` stays null on a save
that predates the table: it was made before anything recorded this, and filling it in with whichever
build happened to run the upgrade would invent the history the column exists to report. Being the
first migration added after all of this, it also put the upgrade path through its paces on something
real rather than on a simulation.

---

## D25 — A guessed owner is recorded as a guess

Two importers invented an owner when the format did not state one: VK produced the fixed identity
`vk:self` and QIP produced `qip:self`, both with `IsSynthetic: false`, and `SeedOwner` linked
whatever it was handed with `confidence='seed'`. So a placeholder was stored exactly as an owner
the export had named.

That is wrong in three ways at once, and they compound:

- **`seed` is what `Unmerge` refuses to detach**, on the grounds that the export itself said so. A
  save whose owner was a placeholder was therefore permanently owned by an account nobody has, with
  no way back through the UI.
- **`is_synthetic = 0` hides it.** The People page's "identified by name only" filter reads that
  column, and it is exactly where someone would go to correct a guess.
- **A fixed id collapses accounts.** Two VK archives from two different accounts both became
  `vk:self`, so one person's messages were attributed to the other.

The rule now: `SeedOwner` writes `'seed'` when the identity is not synthetic and `'auto'` when it
is, and importers mark an inferred owner synthetic. A guessed owner is still *the* owner — message
direction depends on it — but it is detachable, visible as a guess, and scoped to the export it
came from (`qip:folder:<name>`) rather than global.

The better answer is not to guess at all, so `ImportDetection` gained `AccountIdIsGuess` and
`AccountCandidates`, `ImportPreview` carries both, and the import page and `ahistory import --me`
ask. What the user says is stated, not inferred.

---

## D26 — The phantom self-chat, and what a QIP file for your own UIN is

Point QIP at a loose pile of `.qhf` files and the owner became `qip:self`, while the user's real
UIN arrived as an ordinary contact — a `.qhf` header names only the contact. Any file whose header
UIN was the user's own then produced a `dm` thread titled with their own nickname, with `qip:self`
on one side and `qip:<their UIN>` on the other. A conversation with yourself, and nothing in the
schema could tell it from a real one.

**Such a file is Saved Messages.** It holds either messages sent to your own UIN (ICQ let you add
yourself as a contact) or authorization traffic the client filed under your account — already
classified as `service` on message types 5 and 14. Both are yours and neither is a conversation
with another person, so both land in one `saved` thread titled "Saved messages", the same kind
Telegram's have always used (D6). The owner identity is reused rather than a second one built for
the same account, and the file's nickname does not overwrite the owner's display name.

**Owner detection got strict.** It was "the first all-digit folder name within four levels", which
made `backup/2009/*.qhf` an archive owned by "2009" and could just as easily pick a contact's own
folder — attaching a real contact to you as the archive's subject, which §1 names as the merge that
poisons everything downstream, with no confirmation step anywhere. It is now the numeric directory
whose child is called `History`, which is the layout the README documents. More than one answer is
refused by name rather than averaged (D13).

**The invariant, stated as a test:** no import may produce a `dm` thread keyed by one of the
owner's own accounts. Deliberately not "a `dm` whose only participant is the owner" — a
conversation the other person never replied to is exactly that and is perfectly real. Every
platform here keys a direct thread by the person on the other side, so a direct thread keyed by
you is the phantom and nothing else is.

---

## D27 — `.ahf` is named and refused, not parsed

QIP writes `.ahf` when history is archived. The reader globs `*.qhf` only, so a folder of them was
rejected with "does not look like an export this app can read" — which sends someone looking for
the wrong folder — and a folder mixing the two imported half of it silently.

Detection now recognizes `.ahf`, `Read` refuses by name, and a mixed folder imports the `.qhf`
files while reporting what it skipped. **No parser.** Its layout has never been confirmed against
real files, and D22 is the record of what writing a reader from a guessed layout produces: a reader
and its fixtures agreeing with each other while not one real file can be opened. Worth writing when
there are real `.ahf` files to check it against.

---

## D28 — Participants are who was in the room, not who spoke

`thread_participant` was written from observed senders alone, which answers a different question. A
group of eleven where three people ever typed was a group of three; a direct thread you wrote into
and got no reply from had nobody in it at all — and §4's per-person view finds a person's direct
threads through that table, so such a thread never appeared in it. Hangouts states its roster
outright, and it was being parsed and discarded.

`NormalizedThread` gained an optional `Participants`, and the owner joins every `dm` and `saved`
thread whether or not they said anything. Rosters are written once per thread, on its first
message — which is also the only honest timestamp for a stated participant, since a conversation
header carries no join times.

**Telegram's `members` array was deliberately left out.** It names people without ids, so every
group would mint a name-only identity and a person to go with it — hundreds of them across a real
archive, which is the phantom-people problem §2 warns about arriving through a different door. Its
groups keep sender-derived participants until there is a source of real ids.

---

## D29 — Suggested merges, never automatic ones

The plan declined auto-merging and that stands: an under-merge is untidy and one click to fix,
while an over-merge is a confident lie — every fact one person stated becomes a fact about another,
and §7 has no way to tell. But finding the pairs was the user's job, and scanning a thousand
accounts by eye is how a merge feature goes unused.

`MergeSuggestions` pairs identities on a shared handle or a shared display name, ranked, and writes
nothing. Two accounts on one platform sharing a name are *not* offered — Telegram will not let two
accounts share a username and a display-name collision there is a coincidence — unless one of them
is a name with no id behind it (§2), which is a guess by construction.

**Today only the name match can actually fire.** `identity.handle` is populated in exactly one
place — the Telegram owner, from `personal_information.username` — because no export here carries
per-contact handles: Telegram messages give `from` and `from_id` and nothing else, and Hangouts, VK
and QIP have no handle at all. So the strongest rule in the table is unreachable until a reader
exists that supplies them. It stays because it is right when the data arrives and costs a
comparison, but it should not be mistaken for a working signal, and the name rule is what the
feature currently is.

The ranking and its `LIMIT` are computed together, in SQL. They were not at first — the query took
the 50 pairs with the most messages and C# re-sorted *those* by strength, which silently discards a
strong pair that happens to be quiet. That is the wrong way round: a duplicate account somebody
barely used is precisely the one they will never spot by eye, and volume is the tie-break, not the
filter.

What is deliberately absent is fuzzy matching — no diacritic folding, no "Sam" against "Sam Ruiz".
Every miss is an under-merge, which the user fixes in one click; every extra rule buys recall at
the price of the failure mode that cannot be undone by clicking.

`005_merge_suggestions.sql` stores only the rejections. An accepted suggestion needs no row: the
identities are on one person afterwards and the pair stops generating. Rows are keyed by identity
pair rather than person pair, because people come and go as merges happen while an identity is
permanent, and the pair is stored in a fixed order enforced by a CHECK so "A with B" and "B with A"
cannot become two rows.

`IdentityMerger.MergePeople` moves every account at once, which is what someone means when they
have realized two rows are one human; three separate merges leave the archive half-merged between
each. The owner can be merged *into* but never *away*: P5 says a save has exactly one owner, and
dissolving them leaves the archive without a subject.

---

## D30 — No archive-shaped data in the repository

`tests/fixtures` held nineteen files: a Telegram export tree with media, a Hangouts takeout, VK
message pages. All synthetic, and the owner in them was the maintainer's own name.

They are gone, and the rule is the blunt one rather than a judgement about whether a given file is
real. A file inside the source tree that *looks* like an export is one careless copy away from
being one: the tempting way to debug a parser is to drop the file that broke it next to the ones
already there, and the tempting way to fix a failing test is to overwrite a small export with the
large one that reproduces the bug. Both are a single `git add` from publishing somebody's
correspondence — the same thing P6 refuses to let into a log, held to a lower standard because it
was called a fixture.

Export *shapes* now live in `Archive.Import.Synthetic` as builders — Telegram, Hangouts, VK and
QIP — beside `SyntheticExport`, and for the same reason it is there: the CLI, the performance tests
and the unit tests share one statement of what a format looks like. Tests still write real files to
real folders, because reading an export folder is most of what an importer does; the folders are
temporary. `NoArchiveDataInTheRepositoryTests` sweeps the tree for `result.json`, `Hangouts.json`,
`messages*.html`, `.qhf`, `.ahf` and `.db`, and `.gitignore` covers the same patterns plus
`scratch/`, which is where a real archive goes when one is needed for testing.

What this does not change: a builder written from a misreading of a format will agree with a reader
written from the same misreading (D22). Builders keep a confirmed format from drifting. Only a real
export confirms one.

---

## D31 — AI is a switch and a page, not a project boundary

The original rule was that AI lived in its own projects which the spine — including `Archive.Ui`
and `Archive.Desktop` — must never reference. It sounded like the strongest possible reading of P1
and turned out to be the wrong one.

What it actually bought was very little. The thing worth protecting is that a user who never
enables any of this is not shipped a model runtime: `sqlite-vec` and `Whisper.net` are native,
per-RID binaries in a build already over 100 MB per platform, and once they are referenced from
`Archive.Data` they ship and load for everyone. That is a rule about **packages in the storage
layer**, and it survives unchanged.

What the direction rule cost was a settings page nobody could reach. AI has to be switched on
somewhere, which means a page, which means a view model, which means either `Archive.Ui` knows the
AI project exists or an entire plug-in seam is invented to pretend it does not. The second is more
code, more indirection, and no more true.

So: `Archive.Ui` references `Archive.Ai` and AI pages are ordinary pages. P1 is now stated
behaviourally, which is what it always meant — with the switch off there is no model, no process,
no network call, and nothing of it in the rail. That is checked by `AiPageTests` against the real
view models rather than by reading a csproj, and it catches things the old rule never could: a page
that lingers in the rail after AI is turned off, or a reader thrown out of a conversation because
a page appeared above the one they were on.

Two consequences worth writing down:

- **The settings page is always present.** It is where the switch is, and a switch you cannot reach
  is not one. Every other AI page — statistics now, facts and the diary later — is bound to the
  enabled flag and simply is not there when it is off.
- **`Database` will grow an extension hook.** `sqlite-vec` is a SQLite extension and AGENTS.md
  requires every connection to go through `Database`, so the load has to happen there while the
  package must not. The hook is filled in by `Archive.Ai` at startup when AI is enabled, and a save
  opened with AI off never loads it.

`SolutionLayoutTests.No_core_project_takes_an_ai_dependency` is now
`No_storage_project_takes_an_ai_dependency`, joined by `No_storage_project_references_the_ai_project`
— which the package rule would not catch, because talking to an OpenAI-shaped endpoint needs no
package at all.

## D32 — No native model runtime: vectors by brute force, speech and images through the endpoint

The spec names `sqlite-vec` for semantic search (§5) and Whisper.net for transcription (§3), and
D31 was written expecting both. Neither shipped.

**Vectors.** The canonical store was always going to be plain BLOBs with their own dimension
(ai-plan.md §9.1), with a `vec0` table as a disposable index on top. Built that way, the index turns
out to be optional: the sessions worth embedding in one person's archive number in the tens of
thousands at most, and a dot product over that many unit vectors held in memory is a few
milliseconds. So search by meaning is brute force over the BLOBs, cached per model and invalidated
by count. `sqlite-vec` stays the answer if an archive ever outgrows that — at which point the
`Database` extension hook from D31 is where it goes, and still only from `Archive.Ai`.

**Speech and images.** Whisper.net means a native library per platform plus a model file of
hundreds of megabytes that someone has to download, place and update; classic OCR means another.
Both are also worse than what the configured endpoint can already do — a vision model reads a
screenshot better than Tesseract does, and `/audio/transcriptions` is served by OpenAI and by
several local servers. So reading media goes through the same endpoint as everything else, under
the same consent, the same budget and the same statistics, and each is off until its model is named.

What this costs: transcription needs an endpoint that serves it, and LM Studio — the local server
this was developed against — does not. A user who wants fully local transcription today points the
transcription model at a separate local speech server, or goes without. What it buys: the AI half of
the app adds nothing native to the download, `No_storage_project_takes_an_ai_dependency` still has
nothing to catch, and a build that never enables AI is byte for byte what it would be without it.

## D33 — Nine more readers, and what formats without ids or zones cost

Google Chat, Google Voice, Facebook Messenger, Instagram, SMS and MMS, WhatsApp, Skype, Discord and
Slack were added under D20's rule, and again no schema change was needed. Two shared-path changes
were: `ImporterRegistry.DetectAll`, because a Takeout or a Meta download is two or three exports in
one folder and returning only the best match imported part of a history silently (the roadmap's
G1); and `NormalizedMedia.Content`, because an SMS backup carries its MMS pictures as base64 inside
the XML and there is no file to find.

### Formats with no message id

Meta, WhatsApp, SMS texts, Google Voice, and Google Chat exports older than `message_id` give
nothing stable to key a message on. The uid is derived: the conversation, the time, a hash of the
sender and the content, and an **occurrence count of that same combination** — not a position.
Counting by what a message is rather than where it sits means deleting an earlier message before
re-exporting does not shift every later uid. Counts are per file, so two overlapping SMS backups
give a shared message the same uid and it is stored once.

The cost, stated in each of these previews: an edit is indistinguishable from a new message, so the
revision behaviour P2 promises becomes "a changed message is a second message" for these formats.

### Wall-clock time with no zone is taken as UTC

WhatsApp writes times with no zone, and so does Discord's newer package JSON. The roadmap's G4
proposed asking for the export's zone in the preview. It was not built: a single offset is only
right for someone who never travelled and whose country never changed its clocks, so an asked-for
zone would move part of any long archive by an hour or more while looking authoritative. Taking the
times as UTC is what the VK reader already did — the time shown is the time the phone showed —
and each such preview says so.

### WhatsApp's date order is inferred, and refused when it cannot be

`03/04/21` is two different days. The order is decided per chat: a day above twelve settles it;
failing that, the reading that keeps the chat in sequence wins, since the wrong one jumps back months
at every month change; a chat too short to say borrows the order of chats written in the same shape.
Otherwise the import stops and says to export a longer stretch. It never defaults. Dotted dates are
read day first, the only order any locale writes them in, and a two-digit year is this century.

### Other decisions worth keeping

- **Phone numbers lose their formatting and nothing else.** SMS, WhatsApp and Google Voice share
  `SmsBackupImporter.Normalize`. No country code is ever added — which country a local number
  belonged to is not in any of these files. Identities stay per platform, so a number on SMS and the
  same number on WhatsApp are still two accounts until merged (D29); suggesting merges on an exact
  number is the obvious next step and is not built.
- **One JSON, two platforms (G2).** Messenger and Instagram share a reader and keep separate identity
  namespaces. Instagram is claimed only when the download says it is Instagram's.
- **Two shapes, one platform.** Discord's data package and DiscordChatExporter describe the same
  messages with the same snowflakes, so they share `discord` and a message in both is stored once.
  The package holds only the owner's own messages, and its preview says so.
- **Single-file formats are read from their folder (G5 not needed).** Every one here — the SMS XML,
  the WhatsApp text files, Skype's `messages.json` — is found in the folder the user points at, so
  the import page stayed a folder picker.
- **Owners** are read where the format states one (Google Chat, Skype, the Discord package, Meta's
  profile files, Google Voice's "Me", the From address on a sent MMS); inferred as the one person in
  every conversation when there are at least two to compare; and otherwise asked for, under D25.

None of the nine has met a real export. D22 is the standing warning, and WhatsApp — whose format
varies by phone, app version and locale — is where it applies most.

## D34 — The app reads accounts too, not only exports of them

The importer roadmap's recommendation was blunt: *the app imports exports, not devices*. That
stands for everything it was written about — encrypted device databases, passphrases, third-party
tooling — and it is reversed for one case: **an account the user signs in to themselves, through
the platform's own API.**

Two things made the reversal worth it.

**An export goes stale the day it is made.** A Takeout takes days to arrive, a Telegram export is a
manual chore, and nobody repeats either monthly. The archive is a snapshot of whenever someone last
bothered, and the most recent year — the one people actually search — is the one most likely to be
missing.

**An API is the only source of real data this project can get on demand.** Eleven of thirteen
readers have never met a real export (D22, D33). A connected account produces the same messages by
an independent route, which is a check no fixture can be: a builder and a reader written by the same
hand from the same misreading agree with each other. `ahistory sync-check` reads the same messages
both ways and reports every disagreement.

### What was built

**Watched folders**, which need no account at all. A scheduled export — SMS Backup & Restore
nightly, a Takeout every two months — lands in a folder that is re-read when it changes. Re-import
is idempotent (P3), and an unchanged folder costs a directory listing rather than a read, because
`IsAlreadyImported` compares the name-and-size fingerprint the run already records.

**A Telegram connection**, in `Archive.Sync`, which the storage layer must not reference — the same
direction rule that keeps a model runtime out of it (D31), for the same reason: reading a folder
must stay a thing that touches nothing but that folder.

### One normalizer, not two

A message off the wire is written in the shape Telegram Desktop's export writes it, and
`TelegramNormalizer` reads that. Writing a second reader for the API would have meant two
implementations of every §2 trap, and two chances to disagree — and disagreement means the same
message stored twice or looking edited every time the routes meet.

What this cannot establish is whether the export writes a supergroup's chat id the way the API
gives it, or whether entities convert identically. Those need a real export, which is what
`sync-check` is for, and until one has been run this reader is exactly as unproven as the nine in
D33.

### An edit is a change to what was said

`ContentHash` covers each attachment's path, which is right within one route and wrong across two:
an export names `photos/photo_3@…jpg` and an account names `telegram:photo/5566`. A hash mismatch
alone would have recorded a revision, with identical text on both sides, every time the two met.

So a stored message counts as edited when its text, entities or service action differ — entities
compared as JSON values, since an export keeps Telegram's indentation. A different attachment path
is not an edit, and never was a useful one: the media rows such a "revision" would have rewritten
are keyed by ordinal and were not being replaced anyway.

### The cursor and the page are one transaction

Fetching, downloading and hashing happen with nothing open. Then one short transaction writes the
messages and how far the chat has been read. A cursor committed ahead of its page skips that page
for ever after a crash; committed behind it, the worst case is reading a page twice, which the uid
makes free.

This is also why `ImportCommitter` no longer opens its next transaction the moment it commits the
last: harmless for a file read in milliseconds, and a write lock held across every network wait for
a connector.

Catching up has a trap of its own. Asking Telegram for "newer than N" returns the *most recent*
hundred, not the oldest hundred, so a chat with two hundred new messages would leave a hole in the
middle. The cursor walks down from the top of the new stretch and only moves forward once the
stretch is exhausted.

### Chats are decided, never assumed

An account is every channel someone follows as well as everyone they have ever written to. Reading
all of it buries the correspondence under broadcast traffic, so `sync_chat` holds a decision per
chat and nothing is read until it is included. **Undecided is not ignored**: it is listed to be
decided, and including it later reads its history from the start, which is why a live message for
an undecided chat can be dropped without losing anything.

### Deletions are recorded, not applied

The platform says a message is gone; the archive keeps every word of it (P2) and says so, in the
conversation, with the date the deletion was noticed — no platform reports when it happened. A
deletion flushes whatever is buffered first, because a message deleted moments after it was sent is
the ordinary case and marking one that has not been written yet would silently do nothing.

### The session is the account

Whoever holds a Telegram session is signed in as the user. It is kept outside the save (P7, and the
README's promise that a copied save carries no key), DPAPI-encrypted on Windows, and owner-only
elsewhere — the Linux and macOS key stores are native per-desktop libraries, and pulling one in for
a feature most users never enable is what D32 refused for model runtimes. Disconnecting signs out on
Telegram's side rather than only deleting the file.

The `api_id` is the user's own, from my.telegram.org. Shipping one is common for open-source
clients and would run every user's account under the maintainer's application.

### What is deliberately not built

- **Telegram's takeout API**, which is what the official export uses and has looser flood limits.
  It needs a confirmation in another Telegram client and a delay that cannot be tried without an
  account, so it is a known improvement rather than a guess committed blind.
- **WhatsApp through an unofficial client.** It would work — a linked companion device receives a
  history sync — and whatsmeow's issue #810 documents bans landing on low-volume, legitimate use.
  Losing a WhatsApp account costs someone far more than their archive gains.
- **Discord DMs**, which need a user token and are against its terms. DiscordChatExporter already
  exists, and a watched folder picks up what it writes.
- **Signal Desktop**, whose database is SQLCipher with the key in the OS key store: a native
  dependency for one platform's reader.

### Nothing runs while the app is closed

The watcher and the live connection live and die with the window, like the AI runner. Catching up
on the next launch is what covers the gap, and that is a promise about a machine rather than about
a feature.

Neither the connector nor any watched folder has met a real account or a real scheduled export yet.
