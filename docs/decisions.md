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
