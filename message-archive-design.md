# Message Archive — Design Spec

A local-first personal message archive. Imports chat takeouts from multiple
platforms, merges contacts across them, presents one continuous conversation per
person, full-text + semantic search, and an LLM layer that builds a cited
knowledge base and diary for the owner and each contact.

**Stack:** C# / .NET, Blazor frontend, SQLite (FTS5 + sqlite-vec), packaged via
docker-compose. One user per database — switching user means loading another save.
A save is self-contained: `.db` file plus a media folder beside it.

---

## 1. Core data model

The central idea: **separate platform identity from human person.**

- `Identity` — one row per platform account (platform, handle, display name,
  raw platform ID). Messages reference identities.
- `Person` — the merged human. Has `is_owner` bool.
- `IdentityPerson` — link table. Merging contacts = repointing identities at a
  person. Never rewrite message rows.

Consequences: unmerge is trivial (you will need it — auto-matching on
phone/email/name produces wrong merges), and message rows are immutable after import.

Other core tables:

- `Message` — platform, source_message_id, thread_id, sender_identity_id,
  timestamp (+ tz offset), plaintext, reply_to, edited flag, `raw_json` blob,
  `importer_version`.
- `Thread` — DM or group, platform, source id.
- `Media` — content hash, mime, duration, dimensions, original filename.
- `Transcript` — media_id, engine, model, model_version, language,
  per-segment timestamps, confidence, created_at. One media → many transcripts.
- `Import` — source export path, platform, importer_version, timestamp. Every
  message traces back to a row here.

**Rules:**
- Keep the original export file forever, plus `raw_json` per message. When you
  discover the parser dropped reactions, you re-run instead of re-asking for exports.
- Stamp `importer_version` on every row.
- Dedupe on `(platform, source_message_id)`, content hash as fallback. Re-imports
  must be idempotent.
- Content-address media: `media/ab/cd/abcdef….webp`. Exports repeat the same
  stickers and forwards hundreds of times.
- `is_owner` must survive merges. Accidentally merging a contact into the owner
  poisons the entire knowledge base — make owner merges an explicit confirm step.

---

## 2. Import pipeline

Staging → normalize → dedupe → commit. Import finishes in minutes and leaves the
app usable; everything expensive is queued.

**Jobs table** (`status`, `attempts`, `last_error`, resumable across restarts) for
transcription, OCR, embedding, and extraction. A crash at hour six must not restart
the queue. Prioritise recent messages and threads the user actually opens.

### Telegram (first importer)

Export from Telegram Desktop as **JSON**, not HTML. Folder contains `result.json`
plus `photos/`, `video_files/` etc. referenced by relative path — ingest the folder
as a unit. Large accounts split across multiple JSON files.

Traps:
- `text` is *either* a plain string *or* an array mixing strings and objects
  (`{type:"link"}`, bold, mention, code). Ignore it — use **`text_entities`**,
  always the array form. Concatenate `text` values for searchable plaintext, keep
  entities for rendering.
- `from_id` is prefixed (`user123`, `channel456`).
- Service messages use `actor`/`actor_id` instead of `from`/`from_id`. Handle
  `type: "service"` separately or you get phantom people named "phone call".
- `personal_information` at the top gives you the owner — seed the `is_owner`
  Person from it, and use it to map "me" on every later import from other platforms.
- Decide: keep Saved Messages as a "conversation with yourself"? It's usually the
  densest personal-notes archive in the export.

### Other platforms (later)

- **Meta (FB/IG) JSON** — double-encoded UTF-8. Every emoji arrives as mojibake;
  fix at import.
- **WhatsApp `.txt`** — loses reply threading and reactions entirely. Date format
  is locale-dependent; infer DD/MM vs MM/DD per file from days > 12.
- **Signal, iMessage** — not takeouts at all, local databases.
- **Discord** — needs third-party export tooling.

---

## 3. Media, transcription, OCR

Every derived text is an artifact hanging off the media, **never a replacement for
the message**. The message keeps its original empty/caption text as sent.

- **Voice/video → text:** Whisper.net (whisper.cpp bindings), large-v3 fits
  comfortably in 12GB VRAM. Skip diarization for voice notes — sender is already
  known, one speaker. Defer it for multi-speaker video.
- **Screenshots → OCR:** a large share of images in real archives are screenshots
  of other text. Same derived-artifact pattern. Cheap, and makes search feel magic.
- **Photos → captions:** optional vision-model captioning, same table.
- Re-runnable: upgrading the model creates a new `Transcript` row, doesn't
  overwrite. You'll want to diff.

**Flag provenance on the search index row.** A confident transcript, a shaky
transcript, and OCR are three different levels of trust. Results show "from voice
message"; the AI layer weights accordingly. Whisper mishearing a name will
otherwise become biography.

---

## 4. Group chats

Attributed to each person, but **stored once** in their real thread — no per-participant
row copies.

The per-person view is a query: their DM thread, unioned with group messages where
they're the sender. Isolated group lines read as nonsense out of context
("yeah exactly"), so fetch ±N messages around each hit and render them dimmed as
context.

Tag every extracted fact with DM vs group origin. "Sam mentioned he's moving" is
much weaker evidence when said to eleven people.

---

## 5. Search

- **FTS5** for keyword.
- **sqlite-vec** for semantic.
- Hybrid-rank the two.
- Embed **conversation windows** (a session, see below), not individual messages —
  single texts like "ok lol" embed to noise.

---

## 6. AI processing at scale

Never run over the whole pile. Build a tree bottom-up once, patch incrementally after.

### 6.1 Segment into sessions
Split on time gaps (a few hours of silence = new conversation). A session is
typically 10–200 messages and is the smallest semantically self-contained unit.
**This is the most important step** — fixed-size chunks slice through the middle of
exchanges and extraction quality collapses. Sessions are also the unit for embedding
and retrieval.

### 6.2 Filter before spending tokens
A large fraction of sessions are pure logistics ("on my way", "ok", stickers).
Cheap classifier or heuristics (length, unique-token count, question marks, entity
presence) routes those to metadata-only. Typically leaves 20–30% needing LLM work.

### 6.3 Map — extract per session
One call per surviving session, structured JSON out: facts with citations, entities,
events, sentiment. Sessions are independent → fully parallel, resumable from the jobs
table. Pass a small context header (who these two people are, what's already known)
so pronouns and nicknames resolve.

### 6.4 Reduce — fixed hierarchy
Session → month → year → person profile. Each level summarises the level below,
never raw messages. Cache each node keyed by a hash of its children. Re-importing an
old export dirties three nodes, not the archive.

### 6.5 Model split
- **Map** with a small local model (~8B). Mechanical work, 500k of them.
- **Reduce** with the good model. Synthesis across a decade, only a few hundred calls.
- This split is where the cost actually lives.

### 6.6 Versioning
Store `prompt_version` and `model_version` on every derived row. You will change the
extraction prompt at least five times. "Re-run only sessions processed with
prompt < v4" must be a one-line query, not a full rebuild.

**Rough scale:** 500k messages at ~15 tokens ≈ under 10M input tokens. Overnight job
on local hardware, not a research project. Rollups add ~5%. One-time; afterwards only
new messages get processed.

**At query time, retrieve — don't re-summarise.** Hybrid search over sessions, top
~20 with surrounding context. The precomputed tree answers "what's this person like";
retrieval answers "when did we talk about the Prague trip". Same session units for both.

---

## 7. Knowledge base

Owner and contacts use **one pipeline**, not two — the owner is just a `Person` with
`is_owner = true`. Two parallel pipelines drift apart within a month. What differs is
the shape of the evidence.

- Contacts are profiled **per-thread** (usually one conversation).
- The owner is profiled **across all threads** — the same fact surfaces in a dozen
  conversations, phrased differently. Needs a real fact-merge step: cluster
  near-duplicate claims, keep one canonical fact with N citations. Otherwise the
  diary is an unreadable pile of restatements.

### Evidence types (tag every fact)
- **Self-report** — "I've been so busy with the new job".
- **Reflected** — someone else says it to them: "how's the knee holding up?".
  Abundant for the owner, nearly absent for contacts. Captures what people never say
  about themselves. Easy to get wrong: jokes, sarcasm, nicknames read as sincere
  claims. Keep confidence low unless corroborated across senders.
- **Behavioral** — response latency, hour-of-day, frequency, who initiates, gaps.
  Derived from metadata, no LLM, cheap.

### Fact storage
- **Relational facts go on the edge, not the node.** "Met in Berlin in 2015",
  "stopped speaking for two years" belong to the pair, render in both diaries from
  one row. Extracting each side separately produces two contradictory versions.
- **Time validity, not just a timestamp:** `valid_from`, `valid_to`,
  `superseded_by`. "Works at Acme" from 2019 isn't wrong, it's expired — the diary
  needs the old value when rendering 2019 and the current one on the profile page.
- **Contradictions** resolve by recency first, then corroboration count, and stay
  visible rather than being silently dropped.

---

## 8. Diary presentation

The part most likely to go wrong. Two rules make it survivable:

1. **Generate per fixed window** (a month per contact works), cache keyed by a hash
   of the message IDs in that window. New import touching March 2019 regenerates only
   that entry. A diary that silently rewrites your past every time you open it is
   unsettling.
2. **Require citations.** Every sentence carries the message IDs it came from;
   anything emitted without them gets dropped rather than shown. A hallucinated
   detail about a stranger is a bug; a hallucinated detail about the user's own life
   is corrosive — they'll half-remember it as true. Render transcript-derived claims
   with a visible marker.

**Include silence.** "Four months, no contact" between entries is often the most
meaningful thing in a relationship's timeline. A summariser that only describes
messages papers straight over it.

---

## 9. Third-party archives

Mark at save level: `owner_is_self`. Let it change defaults, not just act as a label.

A save built from someone else's archive is a fundamentally different object — a
profile of a person assembled from their private correspondence, with everyone who
wrote to them included, none of whom handed you anything. Be deliberate about which
archives you take on rather than letting the capability decide.

Practically:
- Required provenance note per save: where it came from, who gave it to you.
- Aggressive reflected-evidence inference **off by default** for third-party saves.
  The depth that feels insightful about yourself reads very differently pointed at
  someone who didn't ask.
- LLM provider pluggable, local model as default, API opt-in. This data is about as
  sensitive as personal data gets.

---

## Build order suggestion

1. Schema + Telegram JSON importer + media content-store. Verify idempotent re-import.
2. Blazor thread view — per-person continuous conversation, group context expansion.
3. FTS5 search.
4. Jobs table + Whisper transcription + OCR.
5. Embeddings + hybrid search.
6. Session segmentation + map extraction (small model).
7. Rollup tree + diary rendering with citations.
8. Second importer (WhatsApp or Meta) — this is what proves the schema is right.
