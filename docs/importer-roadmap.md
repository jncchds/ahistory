> **Forward-looking, not a record.** This is an analysis of what it would take to add more
> platforms to the importer, written on 2026-09-10 against the four readers that exist
> (Telegram, Hangouts, VK, QIP). Nothing here is decided. When one of these is built, the
> decisions it forces belong in [decisions.md](decisions.md); this file is the survey that
> preceded them, and it goes stale the moment a real export contradicts it.
>
> Every claim below about a third-party export format comes from documentation and prior art,
> not from a file on anyone's disk. [D22](decisions.md) is the standing warning about exactly
> that.

**Status, 2026-09-11.** Built: G1; Google Chat; Meta (Messenger and Instagram); SMS and MMS;
WhatsApp; Skype's export JSON; Discord's data package and DiscordChatExporter; Slack; and Google
Voice, which this survey did not list. The decisions they forced are [D33](decisions.md). Not built:
Skype's legacy `main.db`, Signal, iMessage, Viber, LINE, WeChat, Miranda and Mail.ru Agent. None of
the new readers has met a real export yet.

---

# Adding platforms to the importer

## What this codebase actually demands of a format

Parsing is rarely the hard part. Four requirements decide whether a format fits at all, in
roughly the order they kill candidates:

| Requirement | Where it comes from | What fails it |
|---|---|---|
| **A stable per-message id** across re-exports | `NormalizedMessage.Uid`; the dedupe and revision path in `ImportCommitter` | Formats with no message id — a re-export must land on the same uid or it is a duplicate storm |
| **Absolute time** | `SentAtUtc` and `SentAtUnix` are both required | Wall-clock-with-no-zone formats |
| **Cheap detection from a folder** | the `Detect` contract; `ImportRunner.Locate` requires `Directory.Exists` | Single-file and archive exports |
| **Refuse rather than guess** ([D20](decisions.md)) | every reader throws on what it does not recognize | Locale-ambiguous formats parsed heuristically |

Everything else costs nothing new. Media by relative path, an export that never names its
account, no reactions, no reply threading — all four are already handled, and
`ImportOptions.OwnerAccountId` plus [D25](decisions.md)'s "a guess is recorded as a guess" make a
silent format a solved problem rather than a blocker.

## The candidates

| Platform | Export shape | Message ids | Time | Media | Verdict |
|---|---|---|---|---|---|
| **Meta (Messenger + Instagram)** | Takeout JSON, `messages/inbox/<thread>/message_N.json` | none | `timestamp_ms`, absolute | yes, relative paths | **Tier 1** — the schema-proving second format §2 always named |
| **WhatsApp** | one `.txt` per chat, media beside it | none | local wall clock, locale-ordered date | yes, by filename | **Tier 1 by value, worst by format** — needs G3 and G4 below |
| **Google Chat** | Takeout JSON, sibling of Hangouts | yes | absolute | yes | **Tier 1** — cheapest real importer; reuses Hangouts concepts |
| **SMS / MMS** (SMS Backup & Restore XML) | one `.xml`, MMS parts base64 inline | none (date + address + body) | epoch ms | inline | **Tier 2, underrated** — see "the payoff nobody has priced in" |
| **Skype** | export tool `messages.json`; legacy desktop `main.db` | yes | absolute | **not included in the export** | Tier 2 — a strong fit for the cohort VK and QIP already serve |
| **Discord** | official package: `messages.csv` per channel, no media, no display names | yes | absolute | no | Tier 2, low payoff. The rich path (DiscordChatExporter) is third-party and needs a token |
| **Slack** | per-channel, per-day JSON; DMs on paid plans only | yes | absolute | URLs that expire | Tier 3 — workspace history, not personal history |
| **Signal** | Desktop SQLCipher database; Android passphrase backup | yes | absolute | yes | Tier 3 — needs a decrypt step outside the importer contract |
| **iMessage** | `chat.db`, plain SQLite, macOS only | yes | Apple epoch nanoseconds; text often in an `attributedBody` blob | yes | Tier 3 — the richest data here, but device access rather than an export |
| **Viber, LINE, WeChat** | encrypted device databases | — | — | — | Tier 3 or never — root or key extraction |
| **Miranda IM `.dat`, Mail.ru Agent** | closed local formats | yes | absolute | partial | Niche, but exactly the QIP cohort — cheap wins later |

## Five things the current contract cannot express

Worth settling before any new reader is written, because four of the five are shared-path
changes that several candidates need at once.

### G1 — A folder can hold two exports, and the registry silently picks one

`ImporterRegistry.Detect` returns the single most confident match. One Google Takeout can
contain both Hangouts **and** Google Chat; one Meta download contains both Facebook and
Instagram messages. A user who points at the root today imports half their history and is told
nothing about the other half — which is the failure [D20](decisions.md) exists to prevent,
arriving through the registry rather than through a reader.

The fix is small: let `Detect` return every non-`None` match, and have the preview say "this
folder contains two exports". This is the highest-impact item in this file, and it is worth
doing whether or not another platform is ever added.

**Done.** `ImporterRegistry.DetectAll` returns every match; the preview names the others, the
import page offers "Read as", and the CLI takes `--format <platform>`.

### G2 — One importer is one platform id, and one format can be two platforms

Meta's Messenger and Instagram threads are the same JSON, but they must not share an identity
namespace: `SourceIdentityId` is only unique within a platform.

**Do not change `IPlatformImporter` for this.** Two thin importers over one shared reader,
distinguished by which activity folder the threads sit under, costs nothing and keeps `Platform`
the simple constant it is. Written down here so it stays a decision rather than something
rediscovered later as a defect.

**Done** as described: `MessengerImporter` and `InstagramImporter` over `MetaMessagesReader`.

### G3 — No stable message id

Meta, WhatsApp and SMS all lack one, so the uid has to be derived — thread, timestamp, and an
ordinal to separate two identical messages in the same second.

The consequence has to be documented rather than glossed: for these formats **an edit is
indistinguishable from a new message**, so the revision behaviour the README promises degrades
to "a changed message becomes a second message". Meta is largely safe, its messages being
immutable in practice; WhatsApp is not. The ordinal is also only stable while each export is a
prefix-extension of the last one — a chat the user has deleted messages from re-imports as new
messages from the deletion point onward.

**Done**, with one change: the ordinal counts repeats of the same time, sender and content rather
than position, so a deletion earlier in a chat does not shift later uids. Used by Meta, WhatsApp,
SMS, Google Voice, and Google Chat exports too old to carry `message_id` ([D33](decisions.md)).

### G4 — Wall-clock time with no zone

`SentAtUnix` is required and non-null. WhatsApp gives `[14/03/2021, 22:41:03]` and nothing else.
Filling UTC in from the importing machine's zone is exactly the "looks right, is wrong" failure
[D20](decisions.md) forbids, and it would stay invisible for years.

The fix that matches existing precedent: extend `ImportOptions` with the export's time zone the
same way `OwnerAccountId` already extends it — asked in the preview, defaulted to nothing,
recorded as a guess under [D25](decisions.md). None of the four current formats needed this;
QIP, the closest in spirit, stores Unix seconds.

**Decided differently.** No zone is asked for. A single offset is wrong for part of any long archive
kept by someone who travelled or whose clocks changed, so zoneless times are taken as UTC — the VK
reader's precedent — and the preview says so ([D33](decisions.md)).

### G5 — Single-file exports

`ImportRunner.Locate` and `Fingerprint` both assume a directory. `.txt`, `.xml`, `.db` and
`.zip` all have to become legal import targets. Mechanical, but it touches the shared path, so
it is done once rather than per importer.

**Not needed.** Every single-file format built so far is read from the folder holding it, so the
import page stayed a folder picker ([D33](decisions.md)).

### And one thing to keep out

Encrypted or device-local sources — Signal, WhatsApp's `msgstore.db.crypt14`, iMessage — need a
passphrase or an OS permission, and `Read(path, sink, options)` has nowhere to put one.

The recommendation is to keep it that way: **the app imports exports, not devices.** Document
the external decrypt step and read the plaintext result. Anything else puts key handling,
platform-specific permission flows and third-party tooling inside an app whose whole claim is
that it works offline on a folder.

## The payoff nobody has priced in: phone numbers

[D29](decisions.md) pairs merge candidates on **display name**, and errs towards under-merging
because names are weak evidence. WhatsApp, SMS and Signal are all keyed on **phone number** — a
strong, globally unique, cross-platform key, which Telegram exports also carry.

So adding one phone-keyed platform does not just add a platform. It turns suggested merges from
a name-similarity guess into an exact join for a large share of contacts. That is the argument
for building the SMS XML reader — small, no media complexity beyond base64 — earlier than its
message volume alone would justify.

## A possible order

1. **G1 and G5 first.** Both are shared-path changes that everything below needs, and G1 is a
   live correctness problem the moment anyone points at a Takeout root.
2. **Google Chat.** The cheapest real importer, sitting next to Hangouts, and it exercises G1 on
   a folder that genuinely holds two formats.
3. **Meta (Messenger + Instagram).** The format §2 always intended as the schema proof. Also the
   first derived-uid reader, and the one that has to fix the double-encoded UTF-8 the spec warns
   about.
4. **SMS / MMS XML.** Unlocks the phone-number identity key above.
5. **WhatsApp.** The highest user value, and last of the tier-1s on purpose: it needs G3 and G4
   settled, and it is the only one where strictness genuinely fights the format. `DD/MM` versus
   `MM/DD` has to be inferred per file from days above twelve, and **refused when undecidable**,
   never defaulted.
6. **Skype**, export JSON plus the legacy `main.db`, for the cohort VK and QIP already serve.
   **Discord** only if someone asks for it.

## The caveat that applies to all of it

Every format above except Google Chat and Meta varies meaningfully across app versions and
locales. Fixtures written from documentation will agree with a reader written from the same
documentation, and both can be wrong together — that is [D22](decisions.md), and it cost a
working QIP reader once already.

For WhatsApp in particular: do not ship without a real export in hand.
