> **A plan, not a commitment.** [`message-archive-design.md`](../message-archive-design.md) §6–§8
> stays authoritative; this document turns those sections into an order of work, and records the
> decisions that have to be made before any of it can be written. Where it departs from the spec,
> the departure belongs in [`decisions.md`](decisions.md) once it is taken.

---

# ahistory — the AI layer

## 0. What this layer is not allowed to do

AGENTS.md P1 is the constraint every choice below is shaped by. It is stated there partly as a
layout rule — AI lives in its own projects that the spine never references — and that half is being
**relaxed**: `Archive.Ui` may reference `Archive.Ai` and AI screens are ordinary pages (§1). What
replaces it is the same principle expressed as behaviour, which is what was actually wanted:

- With AI switched off there is **no model, no process, no network call, and no AI in the UI** —
  not a disabled button, not an empty panel explaining what is missing. The rail does not carry a
  Facts entry.
- Turning it off again is complete: the archive is exactly what it was, and every derived row can
  be deleted in one action (§11).
- With AI enabled and a job running for six hours, reading history is **not slower**. No write
  transaction is ever open across a model call; batches are small and committed short.
- A message with no facts, no transcript and no diary entry looks **normal**, because that is the
  normal state.
- Stopping mid-run leaves a working archive and a resumable job.
- Keyword search works, and works at the same speed, with zero embeddings.

And P6: a log records counts, ids, durations and error types. The AI layer talks to a network
service about the user's correspondence — it is the most tempting place in the codebase to log a
prompt, and the one place where doing so puts private text into a file people attach to bug
reports. `AiLoggingPrivacyTests` runs extraction against an endpoint that fails and one that
misbehaves, and fails if any word anyone wrote — or anything the endpoint said back — reaches the
log.

---

## 1. Solution layout

One new project. `Archive.Ai` holds the config, the provider strategies, the tool runtime, the
prompts, the segmenter, the extraction / rollup / diary pipelines and the job runner; it references
`Archive.Core` and `Archive.Data`. `Archive.Ui` references it and owns the AI pages like any other
page. `Archive.Desktop` gains one `services.AddAi(...)` line.

It is a separate project because it is a lot of code with its own tests, not because the spine is
forbidden to see it. `SolutionLayoutTests.The_desktop_head_depends_only_on_the_ui_and_logging_projects`
is unaffected — the head still reaches AI transitively through `Archive.Ui`.

**What the layout rule is narrowed to.** `No_core_project_takes_an_ai_dependency` stops covering
`Archive.Ui` and `Archive.Desktop` and keeps covering `Archive.Core`, `Archive.Data`,
`Archive.Import`, `Archive.Media` and `Archive.Logging`. The thing it is protecting is not
architectural purity; it is that `sqlite-vec` and `Whisper.net` are **native, per-RID binaries** in
a build that is already 115 MB per platform, and a package referenced from `Archive.Data` ships and
loads for every user whether or not they ever enable anything.

That has one immediate consequence. `sqlite-vec` is a SQLite *extension*, and AGENTS.md says every
connection goes through `Database` — so the extension load has to happen there, while the package
must not. `Database` gains an optional extension hook that `Archive.Ai` fills in at startup when AI
is enabled: the package stays in `Archive.Ai`, the connection stays owned by `Database`, and a save
opened with AI off never loads it.

AGENTS.md P1 is rewritten to match, and `docs/decisions.md` records the change — the layout rule is
being deliberately traded for a behavioural one, and the reasoning has to survive.

**Pages come from the container.** `MainWindowViewModel` stops holding a hard-coded page list and
takes `IEnumerable<ViewModelBase>` ordered by a `Position` on the base class, filtered by an
`IsAvailable` the AI pages bind to the enabled flag. Toggling AI adds or removes rail entries live,
with no restart.

---

## 2. Configuration

**No presets.** The other apps let a user keep several provider configurations and pick one per
bot; here there is one pipeline, so there is one configuration. A preset list would be a table, a
picker, a default-selection rule and a "which preset produced this fact" foreign key, all to
express a choice the user makes once.

### 2.1 Fields

| Field | Notes |
|---|---|
| `Enabled` | Default **false**. Nothing is scheduled, no client is constructed, no page appears until this is on. |
| `DisclaimerAcknowledgedVersion` | Which wording the user accepted. A version, not a bool, so a reworded disclaimer asks again. |
| `Provider` | `Ollama` / `OpenAI` / `OpenAICompatible` / `GoogleAIStudio` — the four families from `achat`, all OpenAI-shaped. |
| `Endpoint` | Empty means the family's default base URL. A filled value always wins, so any family can be pointed at a gateway. |
| `ApiKey` | May be empty; a local model needs none, and a blank key simply omits the header. |
| `MainModel` | Extraction, adjudication, rollups, diary. **Must support tool calling** (§5). |
| `UtilityModel` | Optional (§2.5). Blank means "use the main model", which is the normal case. |
| `EmbeddingModel` | Used at A6. Blank until then is not an error. Changing it invalidates coverage (§9). |
| `OutputLanguage` | The language facts and diary entries are *written in* (§8). Not the language of the archive. |
| Operational | `Temperature`, `MaxTokens`, `TimeoutMs`, `MaxRetries`, `RetryBaseDelayMs`, `RecordPromptBodies`. |
| Runner | `MaxParallelCalls`, `TokenBudget`, `BatchSize`, `RollupDebounce` — policy for the background service (§11.1), not constants in code. |

`MainModel`, `UtilityModel` and `EmbeddingModel` are **freeform text with autocomplete**, never a
closed dropdown. A gateway routinely serves a model it does not list, and a dropdown built from
`/models` turns that into "your model does not exist". Suggestions are the union of the last
successful `/models` fetch (cached), a small curated list per family, and what the user typed
before.

### 2.2 Where it lives

**In the per-user config directory, shared across saves** — `ahistory/ai.json`, with `AHISTORY_Ai__*`
environment overrides like everything else. A save is a `.db` plus a media folder, designed to be
copied between machines and sometimes handed to someone; an API key inside it travels with the
correspondence. Sharing one configuration across saves is also simply what a user wants: the
endpoint and the model are a property of their machine, not of an archive.

Two things follow from the split:

- **Enabled is a machine setting; facts are a save setting.** A save carries a per-save
  `ai_opt_out` flag, which §9 of the spec wants anyway for third-party archives — a save built from
  someone else's correspondence should not start being profiled because the machine has AI on.
- **A save explains itself on another machine.** `prompt_version`, `model` and `model_version` are
  recorded on every derived row, so a fact opened elsewhere still says what produced it even though
  the configuration that produced it is not there.

The key is stored in plaintext, the UI says so in one sentence, and it is never written to a log,
never to `ai_interaction`, and never shown again after saving — the field renders as "set" with a
Replace button. OS keychains (DPAPI / Keychain / libsecret) are three platform integrations plus a
headless-Linux fallback; worth doing later, not worth blocking on.

**Validation at startup**, like `ArchiveOptions.Validate`: enabled with a blank main model, or a
malformed endpoint, throws with a sentence naming the setting.

### 2.3 The disclaimer

Enabling AI shows, and requires acknowledging, roughly this:

> Everything on these pages is produced by a language model reading your messages. It will be
> confidently wrong about some of it, and how wrong depends on the model you chose. Facts carry the
> messages they came from — check them. If you have configured a hosted provider, your messages are
> sent to it.

Two things it must actually say, because they are the two a user cannot discover for themselves:
accuracy depends on the model they picked, and whether text leaves the machine depends on the
endpoint they picked.

### 2.4 Load models, and the tool-calling probe

**Load models** calls `ListModelsAsync`, fills the autocomplete source, caches the result with a
fetched-at timestamp. Failure is a message on the page, not an exception.

**Test connection** does two calls, not one:

1. A trivial completion — proves endpoint, key and model name.
2. A trivial *tool* call — a `ping` tool with one argument, and a check that the model returned a
   tool call rather than describing one in prose.

The second is there because the entire write path is tool calls. A model without them produces a
run that looks healthy, costs an hour and writes nothing. Discovering that on the settings page
costs two seconds. If the probe fails, the page says the model does not appear to support tool
calling and extraction refuses to start; the config still saves.

### 2.5 The second model

§6.5 splits the work: a small local model for the half-million map calls, a good model for the few
hundred reduce calls, and that split is where the spec puts the whole cost argument. One model name
cannot express it.

`UtilityModel` is one more freeform textbox on the same provider and endpoint — not a preset, not a
picker — used for extraction and adjudication when set. It is **optional and blank by default**,
because locally a second model means either a second resident model or a swap on every call, and a
single larger model usually beats both. It pays for itself against a hosted provider, or against a
machine with room for both.

---

## 3. Providers

Copied from `achat` (`OpenAiShapedProvider`, the four strategies, `LlmProviderFactory`,
`LlmProviderException`) — already plain `HttpClient` + `System.Text.Json` with no SDK, and already
carrying the retry policy worth keeping: transient means connection error, timeout, 408, 429, 5xx;
a 4xx surfaces immediately; a user cancellation is never retried and propagates unchanged.

What changes on the way in:

1. **`LlmPresetConfig` → `AiConfig`.** Same shape minus the preset identity.
2. **Tool calling** — the addition. `LlmChatRequest` gains `Tools` (name, description, JSON-schema
   parameters) and `ToolChoice`; `LlmCompletion` gains `ToolCalls` (id, name, raw argument JSON);
   `MessageRole` gains `Tool`, and a tool-result message carries the call id it answers.
3. **Streaming is copied but unused.** Background extraction has nobody watching tokens arrive, and
   a tool loop over a stream is materially more code. It stays for a future "ask my archive".
4. **Privacy.** The provider logs family, model, endpoint *host*, status, token counts, duration,
   attempt number. Never messages, never tool arguments, never the key.

---

## 4. Interaction stats

One row per model call, success or failure, in the save: `ai_interaction`.

Recorded always: `created_utc`, `purpose` (`extract` / `adjudicate` / `rollup` / `diary` / `embed`
/ `list_models` / `test`), `provider`, `endpoint` (scheme + host + path, no key, no query), `model`,
`prompt_tokens`, `completion_tokens`, `total_tokens`, `duration_ms`, `http_status`, `failed`,
`failure_kind`, `finish_reason`, `tool_call_count`, `attempt`, and the subject
(`session_id` / `person_id`).

**Request and response bodies are configurable and off by default.** `achat` stores them and is
right to — there, "why did my friend say that" is the product. Here the request body is the user's
correspondence, and writing it into a second table means the same private text exists twice in the
save in a form that is much easier to read out of by accident. `RecordPromptBodies` turns it on for
debugging a bad prompt, the settings page says plainly what it does, and it is off again by default.

Retention: pruned on write to the most recent N rows or M days. This table is derived — clearing it
deletes all of it and affects nothing else.

The stats page answers: how much has been processed, how much is left, how many tokens that cost,
the failure rate and the failure kinds, and throughput. **No pricing.** Guessing per-token prices
for an endpoint the user configured produces a confident wrong number.

---

## 5. The tool set

The extraction model does not return a JSON document that the runner parses and trusts. It **calls
tools**, each call is validated, and an invalid call comes back to the model as a tool result it can
correct. Three reasons this is the right shape here and not just fashion: a malformed blob fails
wholesale while a bad call fails alone; the model finds out that message id 8231 is not in this
window while it can still fix it; and the tool arguments *are* the audit record, one call to one
row.

### 5.0 A note on the filter's thresholds

§6.2's numbers — 20–30% of sessions surviving — are the spec's estimate for a real archive, and the
thresholds in `SessionFilter` have **not been measured against one**. On synthetic data they pass
95%, which says more about a generator drawing from thirty-seven varied phrases than about the
rules: a synthetic session is mostly interesting lines by construction, and tuning against it would
be D22's mistake in a new place — a generator and a classifier written from the same guess agreeing
with each other.

What is settled is the shape. A flat distinct-word threshold is a threshold on session *length*
wearing a disguise, so the count is per message; a single substantial message carries a session on
its own; and a question lowers the bar, because "how did it go with your mother?" is short and is
the most useful line in a week of logistics. **Before A3 spends tokens, run the filter over a real
export and look at what it drops.** The version on every session is what makes that cheap: change
the rules, and the archive re-queues itself.

### 5.0b The schemas have to be flat

Written as nested — `subject: {person | pair}`, `asserts: [{message_id, quote}]` — the tool schemas
read beautifully and **crash a local model server**. A runtime like llama.cpp compiles a tool
schema into a decoding grammar, and on LM Studio with a 31B model that compilation aborted with
*bad allocation*, taking the engine down with it; every request afterwards returned
`fetch failed` until it reloaded.

So: every argument is a string, a number, or an array of one of those. A pair is
`subject_person` plus an optional `subject_person_b`; citations are `message_ids` as a flat list
of integers with one optional `quote` beside them; a validity date is `valid_from` and
`valid_from_message_id` side by side rather than an object. Less elegant to read, and the
difference between working on a local model and not — which is the deployment §2.3 calls the
default.

**This did not fix that particular box.** With the flat schemas, the same endpoint still dies on
*system prompt + transcript + any tool at all*, while each half alone succeeds — around 900 tokens
with a grammar attached. That is a limit of that runtime rather than of this format, and the
symptom is worth writing down because it arrives as an HTTP **400**, which the retry policy
correctly does not retry: a run against such an endpoint fails three times per session and lands
in `failed`, which is where it should be visible.

### 5.1 Write tools — extraction

- `record_fact(subject, predicate, object, claim, confidence, asserts[], valid_from?, valid_to?)`
  where `subject` is `{person: id}` **xor** `{pair: [id, id]}` (§7: relational facts live on the
  edge), `asserts` is a list of `{message_id, quote?}`, and each of `valid_from` / `valid_to` is a
  date plus the message id establishing it — which is what writes the `establishes_valid_from` /
  `establishes_valid_to` citation roles.
- `corroborate_fact(fact_id, message_ids[])`
- `contradict_fact(fact_id, message_ids[], note)` — contradictions stay visible (§7), not silently
  dropped.
- `supersede_fact(fact_id, new_object, new_claim, message_ids[], valid_to_message_id)` — the outcome
  neither embeddings nor key matching can produce. "Works at Acme" from 2019 is not wrong, it is
  expired.
- `nothing_to_record(reason)` — an explicit, cheap exit. Without it, a model handed a session of
  "on my way" / "ok" will find something to say, and §6.2 says most sessions are exactly that.

**Two arguments the model is deliberately not asked for.** `origin_kind` (DM vs group) is a
property of the thread, and `evidence_kind` (self-report vs reflected) follows from who sent the
cited message relative to the subject. The runner derives both; asking the model adds two fields it
can get wrong and nothing it can get right. `behavioral` evidence never comes from a model at all —
it is metadata, computed, free.

### 5.2 Read tools — rollup and diary

Retrieval, not re-summarisation (§6): `get_facts(person, as_of?)`, `get_messages(ids)`,
`search_messages(person, query, limit)`. The `as_of` is assertion time, so a 2019 diary entry is not
narrated with what we came to believe in 2023.

### 5.3 Write tools — diary

`write_diary_entry(person_id, window_start, window_end, text, citations[])` and
`revise_diary_entry(entry_id, text, citations[], what_changed)`. Silence is *given* to the model in
the context header, computed from the gaps — a summariser asked to notice absence papers over it.

### 5.4 Validation, and what happens when it fails

Every call is checked before anything is staged:

- cited message ids exist, fall inside the window being processed, and belong to a thread the
  subject is in;
- subject person exists; a pair is canonically ordered (`a < b`) so the edge cannot exist twice;
- `confidence` in [0,1]; `predicate` matches `^[a-z][a-z0-9_]*$`; claim and object length-capped;
- `valid_from <= valid_to`; a future `valid_from` is allowed ("moving in June") but capped;
- a value that only repeats the key (`landed` for `landed`) is refused — found on the first real
  run, where "just landed" was recorded at 0.9. A fact with nothing to put in its value is almost
  always a moment rather than something true of a person;
- **a sentence with no citation is dropped, not shown** (§8 of the spec).

A failed call returns its reason as the tool result. After a bounded number of correction rounds the
session is marked `needs_review` and the job moves on — a stuck session must not stall the queue,
and silently discarding it hides a prompt regression.

What is checkable is mechanical: that the ids exist and sit in the window. That a sentence *follows*
from those messages is not, and pretending otherwise is worse than admitting it. The citation rule
catches the lazy failure; the confident one is caught by a person reading the panel, which is why
every fact renders with its messages one click away.

### 5.5 Transactions

Tool calls accumulate in an in-memory staging list. When the model finishes — or the round cap is
hit — **one short transaction** writes the `derived_artifact`, the facts, the citations and the job
completion together. Nothing is written mid-conversation, and no transaction is ever open while a
call is in flight. This is P1's hard rule and the reason the archive stays readable during a
six-hour run.

### 5.6 Re-running, and what a user's own edit outranks

A job is keyed by `(kind, subject_id, prompt_version, model_version, input_hash)`. Re-running with
the same key is a no-op — that is §6.6's "re-run only what was produced with prompt < v4" as a
`WHERE` clause.

Re-running with a **new** prompt version writes a new artifact and new facts, and closes the previous
run's facts in *assertion* time (`retracted_utc`), not event time. They stop being believed; they do
not stop having been believed. This is what the two time axes in `002_derived.sql` are for, and
getting it wrong looks like a diary that quietly rewrites 2019 — the thing §8 calls unsettling.

**A user's correction is not a model output and a re-run must not touch it.** `fact` gains a
`source` (`extracted` / `user_edited` / `user_deleted`), and:

- a `user_edited` fact is never retracted by a re-run;
- a `user_deleted` fact is a tombstone, not a missing row — a re-run that would re-assert the same
  normalized `(subject, predicate, object)` records the citation and stays deleted;
- the facts panel shows which is which, because "the model said this" and "I said this" are
  different claims about the same sentence.

Without this, the first prompt improvement silently reverts every correction the user made, which
is the fastest possible way to make the panel not worth correcting.

**A session ceasing to exist must not delete a correction either.** The schema made that the
default: `derived_artifact.source_session_id` cascades, and so does `fact.derived_artifact_id`, so
a thread gaining one message would re-segment, delete the session the message landed in, and take
with it every fact read from that session — the user's included. Sessions are therefore retired
rather than deleted outright: in one transaction, the model's facts from the old session are
retracted, its artifacts are detached from it, and only then is the session row removed. The
user's rows stand; the new session is read afresh.

---

## 6. Prompts

Embedded resources in `Archive.Ai/Prompts/`, one file per prompt, each with a version string in a
manifest: `extract.session`, `adjudicate.fact`, `rollup.month`, `rollup.year`, `diary.window`.

**`PromptTests` pins the SHA-256 of every shipped prompt version**, exactly as `MigrationTests` does
for migrations and for the same reason: `prompt_version` is recorded on every derived row, so a
version has to mean one text forever. Editing a prompt without bumping its version silently makes
"processed with v3" mean two different things, and no query can tell them apart afterwards. Changing
a hash to make the test pass is the bug the test exists to catch.

Every prompt gets a **context header** (§6.3): who these people are, the window, the top live facts
already known about the subject, the silence around the window, the output language, and the rule
that a claim without a message id will be dropped. Pronouns and nicknames do not resolve without it.

The extraction prompt states, at minimum:

- record what a person said about themselves, or what was said to them — not what they discussed;
- a joke, a quote, sarcasm and a hypothetical are none of these;
- a line said in a group is weak evidence and is marked as such;
- never invent a message id; cite only ids from the transcript above;
- if this session contains nothing worth recording, say so with `nothing_to_record` — a correct and
  common outcome, not a failure.

That last line is load-bearing. Most sessions are logistics, and a model that believes
empty-handed means failure will fill the fact store with "Sam said he was on his way".

---

## 7. Coverage — what has been processed, and what has not

"Processed" is not a boolean; it is a boolean *at a version*. A session extracted at prompt v3 with
model X becomes unprocessed the moment either changes, and a UI that says "done" without saying at
what is lying by omission.

What is tracked, from `ai_job` joined to `session`:

- **Per save and per person**: `n of m sessions extracted at the current prompt`, plus the counts
  behind it — never attempted, superseded by a version change, failed, `needs_review`, excluded.
- **`needs_review` is its own bucket** with a page, not an error swallowed into a percentage. It is
  where a prompt regression becomes visible.
- **In the conversation, only when AI is on**: a discreet marker on a stretch that has not been read
  yet, so an empty facts panel for March 2019 is distinguishable from "nothing was said in March
  2019 worth recording". Those two look identical otherwise, and the difference is the whole
  question a reader has.
- **Embedding coverage is counted separately** and against the *current* embedding model (§9).

A person's page shows this as a line, not a dashboard: what fraction of them the model has read.

---

## 8. Output language

One setting, applied to what the model *writes* — `claim_text`, diary prose, rollup summaries — and
not to what it reads. An archive is routinely mixed-language; the facts about it should not be.

- Recorded per row: `derived_artifact.language` already exists for exactly this.
- Changing it does **not** rewrite existing facts. It marks them, and the UI offers regeneration of
  a person or a window rather than silently retranslating a decade.
- `predicate` and `object_text` stay normalized and English regardless — they are the merge key, and
  a merge key that changes with a UI setting is not a key. Only `claim_text` is prose.

---

## 9. Embeddings — storage, and changing the model

### 9.1 Storage

**The canonical store is a plain BLOB with its own dimension.** `embedding(session_id, model, dim,
vector BLOB, created_utc)`, unique on `(session_id, model)`. `sqlite-vec`'s `vec0` tables fix
dimension at table creation, so they cannot be the store — a `vec0` table is an **index**, created
per model, dropped and rebuilt from the BLOBs at will. Any model, any dimension, and the index is
disposable. For a small archive, brute-force cosine over the BLOBs is fine and needs no extension
at all.

### 9.2 Configured, and active

The setting names the **configured** model. Search uses the **active** one: the newest model that
has *complete* coverage. Normally they are the same. While a new model is being built they are not,
and that gap is the entire point.

Embedding spaces are not comparable, so a ranking cannot mix two models — but nothing stops both
sets of vectors existing. So changing the embedding model:

- keeps semantic search working the whole time, in the old space, at full coverage;
- switches atomically the moment the new model finishes, with no interval where the feature is
  degraded or absent;
- costs nothing to abandon halfway — the old vectors were never touched, and reverting the setting
  makes the old model configured *and* active again immediately;
- is a few MB of duplication per model, which is not a number worth designing around.

The settings page states the consequence before the change is saved — *N sessions are indexed with
`old-model`; `new-model` will be built in the background and take over when it is complete; search
keeps working meanwhile* — and the enqueue confirmation of §11.2 carries the cost.

Clearing the vectors of a model that is neither configured nor active is a separate, explicit
action, next to "forget everything" (§11.4).

---

## 10. Search with AI on

§5 of the spec: FTS5 for keyword, vectors for semantic, hybrid-rank the two, and embed **sessions**
rather than messages because "ok lol" embeds to noise.

Two constraints on doing it:

- **Keyword search is the baseline and does not get slower.** With AI off, the search path is byte
  for byte what it is today — no branch that loads an extension, no join that has to be excluded.
- **Zero coverage is silent.** If nothing is indexed with the current model, search runs as keyword
  search and says nothing about it. An empty semantic pane explaining what the user is missing is
  exactly the failure P1 names.

Shape: hybrid ranking is a toggle in the search bar, on by default once coverage passes a
threshold, and a result carries a badge saying which half found it — a semantic hit with no keyword
overlap is the interesting case and also the one most likely to be nonsense, so it should be
identifiable at a glance.

A natural-language "ask my archive" over the same retrieval is deliberately out of scope until the
tree exists. At query time, retrieve — do not re-summarise (§6).

---

## 11. Running it: the queue, the confirmation, and reversal

### 11.1 There is no indexing mode

Every unit of work — segment, extract, embed, roll up, write a diary entry — is a row in `ai_job`,
and a **background service** drains the queue whenever AI is enabled. There is no first-run wizard
and no separate re-index flow, because both would be the same code with a different button on it.
The archive is either caught up or has a backlog, and the UI shows the backlog.

**Work is enqueued by invalidation, never by a button.** Enabling AI, finishing an import, bumping
a prompt version, changing the embedding model, un-excluding a person — each enqueues exactly the
jobs it invalidated. The controls a user has are *start*, *pause* and *how fast*.

**Ordering: newest first, and by the people they actually write to.** A ten-year archive drained
oldest-first shows nothing useful for hours, which reads as broken. Priority is a function of
session recency, how much a person is written to, and whether their page was opened recently — so
the facts panel has content within minutes and improves continuously. It is the same total work in
a different order, and the difference is whether anyone waits for it.

**Rollups and the diary debounce.** A dirty month waits until every session beneath it is clean
*and* the subtree has been quiet for a while. Without that, a re-index rewrites the same diary entry
forty times as its sessions land one by one — on the expensive model (§6.5), which is where that
becomes a real number rather than an inefficiency.

**From configuration**: `MaxParallelCalls`, `TokenBudget` (the run stops when it is reached, rather
than reporting it afterwards), the retry and timeout settings. Also from configuration, because
they are policy and not code: batch size and the debounce interval.

As built: `MaxParallelCalls` is honoured — model calls run that many at once, local work one at a
time, and the setting is re-read on every claim. `TokenBudget`, `BatchSize` and
`RollupDebounce` exist in the settings but **are not enforced yet**; none is on the settings page,
so nothing claims otherwise. `TokenBudget` is the one that matters before anyone points this at a
paid endpoint.

**Robustness.** Per-job exponential backoff, an attempts cap, then `needs_review` (§7). Jobs left
`running` by a crash are reclaimed at startup by lease age. The runner writes in small batches with
short transactions and never holds one across a model call — P1's hard rule, and the reason reading
stays fast during a six-hour drain.

The runner lives and dies with the app. There is no daemon, no scheduled task, and nothing that
processes the archive while the app is closed.

### 11.2 One confirmation, and it is where the preflight lives

Any action that would enqueue more than a few hundred jobs shows a single dialog stating **scope,
cost and destination**:

> *This will process N sessions across M conversations — roughly T tokens, about H hours — and send
> them to `api.example.com`.*

That is the cost preview and the hosted-endpoint warning in one place, which is right, because they
are the same question. The endpoint sentence is omitted for localhost and shown every time for
anything else — this is the moment where the README's privacy claim is either true or decoration.

**Estimates come from measurement.** After the first ~50 calls, `ai_interaction` knows tokens and
seconds per session for this model and this archive. Before that the dialog says *unknown* rather
than inventing a number, because a made-up ETA that is wrong by 10× is worse than no ETA.

**A prompt bump enqueues itself; a main-model change does not.** A shipped prompt version is our
change and presumably an improvement, so it queues. Swapping the main model makes every fact
technically stale, but re-extracting a decade because someone tried a different model is punitive —
so it is *marked* (§7 counts it as a different generation) and the user re-runs per person or
wholesale when they mean to.

**The yes is checked when a job runs, not when it was queued.** The first version checked only
when planning, and running the real app showed what that allows: a save that already carried
extraction jobs was drained on launch against whatever endpoint was configured at that moment, with
nobody asked. Agree for a local model, queue a thousand sessions, point the settings at a hosted API
and restart — and the queue would have gone there. Now every claim skips any kind of work whose
handler says it may not run: an unconsented extraction job stays pending, unclaimed, with no attempt
used and nothing sent, while segmentation carries on around it. And the activity page asks the
question on its own whenever such work is waiting, because the runner idles between jobs and a
Start button hidden while it "runs" left no way to give the answer.

### 11.3 Scope

- **Excluding a person or a thread.** Some correspondence should not be profiled at all, and with a
  hosted endpoint that also means never sent. Per-person and per-thread exclusion, plus the per-save
  `ai_opt_out` of §2.2. Cheap now, awkward to retrofit once jobs exist.
- **Third-party saves (§9 of the spec).** When `owner_is_self` is false, reflected-evidence
  inference is off by default. The depth that feels insightful about yourself reads very differently
  pointed at someone who did not ask.

**As built:** leaving a person out is a button in the facts panel. Their direct conversations are
never queued or read, and their lines are dropped from any group transcript before it is sent, so
nothing they wrote leaves the machine; letting them back in re-queues what was skipped. The first
version only took them off the roster, which would still have sent their direct conversations —
caught while building the button. Thread exclusion and the per-save opt-out are honoured but have
no control yet, and the third-party default is not built.

### 11.4 Forget everything

One action that deletes every derived row — artifacts, facts, citations, embeddings, jobs,
interactions — and leaves the archive untouched. It is what makes enabling AI reversible, and it is
how a user recovers from a prompt that produced garbage.

**It confirms, and the confirmation is typed, not clicked.** The dialog names what goes (N facts, N
diary entries, N embeddings, every statistic) and what stays (every message, every media file,
search), and requires typing the word to proceed. A single misplaced click must not be able to
discard a week of processing, and an OK button is one misplaced click.

Narrower forms sit beside it and need only a normal confirmation, because they are recoverable by
re-running: this person only, embeddings only, statistics only, the vectors of an unused model.
Deletion runs in one transaction.

**As built:** forgetting everything is on the AI page — there even with AI switched off, which is
when someone most wants it — with the counts in front of the person and the word typed. Settings,
consent and exclusions are kept: they are the user's choices, not the model's output. Of the
narrower forms, only clearing statistics exists.

---

## 12. What a new import does

Enrichment is incremental or it is unusable: a re-import must dirty a few nodes, not the archive
(§6.4).

- New messages land in sessions; the segmenter re-segments only the **threads touched by the
  import**, and only the time range they touched.
- A changed session's `member_hash` changes, which invalidates its extract, which dirties the month
  above it, the year above that, and the person profile — and nothing else.
- A re-import that changes nothing (P3) must produce **zero** dirty nodes. That is a test, and it is
  the same test in spirit as `Re_importing_the_same_export_changes_nothing`.

Without this wired from the start, the first re-import quietly reprocesses a decade and the user
finds out from the token bill.

---

## 13. Schema

Append-only and hash-pinned, like every other migration — and **one migration per phase**, not one
for the whole layer. A table written now for a queue that does not exist yet would be a schema
guess pinned by a hash, and the only way to correct it afterwards is another migration anyway.

`006_ai_interaction.sql` shipped with A1 and carries `ai_interaction` (§4) alone. What the later
phases add:

- `ai_job` — the queue: kind, subject id, state, `priority`, attempts, `lease_utc`,
  `prompt_version`, `model_version`, `input_hash`, timestamps, last error kind. Resumability is this
  table existing; `lease_utc` is how a crash is distinguished from a job still running (§11.1).
- `embedding` — §9: `session_id`, `model`, `dim`, `vector BLOB`, unique on `(session_id, model)`.
  No `vec0` table in the migration; the index is created and dropped at runtime per active model.
- `fact` gains `source` (`extracted` / `user_edited` / `user_deleted`) — §5.6.
- `derived_artifact` gains `source_person_id`, `source_edge_id`, `window_start_unix`,
  `window_end_unix`. The table was written for artifacts hanging off media, messages and sessions;
  a rollup and a diary entry hang off a **person and a window**, the one shape it cannot currently
  express. Cheap now as `ALTER TABLE ADD COLUMN`; it is the only gap the V1 seams left open.
- `save_meta` (or `save_provenance`) gains `ai_opt_out`; `person` and `thread` gain
  `ai_excluded` — §11.
- Indexes for "the live facts about this person", "the diary entry for this person and month", and
  the coverage counts in §7.

Map every new column in `ArchiveDbContext` — `EfSchemaTests` checks both directions.

---

## 14. Order of work

Each step ends with something demonstrable (P4), and each is harmless if the next is never built.

- **A1 — Config, providers, stats. Done.** `Archive.Ai`, the settings page, load models, test
  connection with the tool probe, `ai_interaction`, the stats page, pages appearing and
  disappearing with the enabled flag. **Touches nothing in the archive.** Demonstrable: configure a
  local endpoint, test the connection, watch the two calls land in the stats page, switch AI off
  and watch the rail return to what it is today.
- **A2 — Sessions, the queue and coverage. Done.** Time-gap segmentation per thread (§6.1), the
  cheap logistics filter (§6.2), `ai_job` and the background runner of §11.1 with segmentation as
  its first job kind, the coverage counts of §7. **No model involved** — which is what made it the
  right place to build the runner: pause, resume, crash recovery, priority and progress were all
  exercised before a single call cost anything. Demonstrable with `ahistory ai <save.db> --segment`.
- **A3 — Extraction. Built; not yet seen working against a real model.** The tool runtime, the
  extraction prompt (hash-pinned like a migration), extraction as a second job kind, facts and
  citations, the exclusions of §11.3 and the per-save opt-out. The facts panel beside a
  conversation, every fact one click from its message, editable and removable. The confirmation of
  §11.2, asked once per endpoint. Extraction queued on its own after segmentation — but only for an
  endpoint the user has said yes to. What is still open: a real extraction run end to end, which the
  local test endpoint could not do (§5.0b); and the panel has been checked by its view-model tests
  but not yet looked at.
- **A4 — Merge.** Candidate retrieval by normalized key first, adjudication for the ambiguous band,
  three outcomes. An over-merge is a confident lie with twelve citations; an under-merge is merely
  ugly. Bias accordingly.
- **A5 — Rollups and diary.** Session → month → year → profile, cached on the fact set visible to
  the window and not only on its messages, revisions shown as revisions.
- **A6 — Embeddings and hybrid search.** §9 and §10, and the first native package — the first time
  the narrowed layout rule has anything to catch.
- **A7 — Transcription and OCR.** Independent of everything above and orderable anywhere;
  Whisper.net, a derived artifact per media, provenance flagged on the search row.

A6 and A7 are also where "the archive runs with no AI component" stops being free and becomes a
packaging question — both are native, per-RID, and land in a build that is already 115 MB.
