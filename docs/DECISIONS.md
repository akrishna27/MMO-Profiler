# Decisions

Newest at the bottom. Each entry says what was decided, why, and what would justify revisiting it. Add an entry whenever a decision is made or reversed.

## D1. Game-agnostic core with thin game adapters (2026-11-09)

**Decision.** Four projects: `MmoProfiler.Core` (no dependencies), `MmoProfiler.Storage.LiteDb`, `MmoProfiler.Games.Ffxiv` (pure mapping, no Dalamud), and `MmoProfiler.Dalamud` (the only project that references Dalamud).

**Why.** The user wants the app portable to other MMOs. Dalamud types cannot be unit tested outside the game, so everything testable has to live outside the plugin project.

**Note on World of Warcraft.** WoW addons are sandboxed Lua: no .NET, no HTTP, no local model. A WoW port is therefore a small companion program that tails the game's chat log file and feeds the same core. The user accepted this as what "portable" means.

## D2. Only tone and intent come from the model (2026-11-09)

**Decision.** Character, channel and time are taken from chat metadata. The model only supplies tone, intent, language and topics, plus a rolling summary.

**Why.** The original idea had the model identify all of these. The metadata is exact and free; using the model for it would cost time and add errors.

## D3. Ollama over HTTP, not in-process inference (2026-11-09)

**Decision.** The only AI backend in v1 is an Ollama server on the user's machine, called over localhost. It is a documented prerequisite in the README. The analyser sits behind `IMessageAnalyzer` and `IProfileSummarizer`.

**Why.** Running a native inference library inside the game process risks crashing the game and competes with it for VRAM. The user initially wanted the model contained in the plugin, then confirmed that a standard Ollama install with README steps is acceptable.

**Revisit if** users find the Ollama setup too hard. A second backend can be added behind the same interfaces.

## D4. Batch processing on a background task (2026-11-09)

**Decision.** The chat callback only enqueues into a bounded queue that drops the oldest message when full. A background task runs a cycle every 60 seconds, or sooner when 100 messages are waiting, with one Ollama request in flight at a time.

**Why.** The chat event fires on the game's framework thread; anything slow there causes stutter. The user asked for periodic batches specifically to protect performance.

## D5. Deterministic stats are recorded even when the model is down (2026-11-09)

**Decision.** Message counts, channels and activity times are always recorded. If the analyser fails, the remaining chunks in that cycle are skipped and those messages are never retried. A circuit breaker pauses model calls after three consecutive failures.

**Why.** Not retrying keeps memory bounded and the logic simple. Profiles stay useful without Ollama.

## D6. Not targeting the official Dalamud repository (2026-11-09)

**Decision.** Distribution is by dev-plugin path now and a custom repository URL later (Dalamud settings, Experimental tab).

**Why.** The user first wanted eventual official submission and pointed to XIVLogger as precedent. The published rules (read on 2026-11-09 at dalamud.dev/plugin-publishing) do not mention chat at all, but three of them put acceptance at risk:

- The AI usage policy: "Entirely AI-generated submissions will be auto-rejected", and AI use must be disclosed. This codebase is model-generated.
- Plugins must not "collect account IDs of player characters beyond your own in any form". We never do (see D7), but whether name-keyed profiles are acceptable is undefined.
- No "information beyond what is traditionally available to players". Review is subjective and any team member can veto.

The user then chose to skip the official repository.

**Revisit if** the user wants official submission. Ask in the Dalamud Discord first, as their docs advise, and expect to rework the code by hand.

## D7. Identity is name plus home world (2026-11-09)

**Decision.** `CharacterKey` is `gameId:Name@Realm`, compared case-insensitively. No account ID or content ID is ever read or stored.

**Why.** Chat does not carry a stable ID, and collecting account IDs is against Dalamud's rules and the project's privacy stance.

**Known limitation.** A name change or world transfer produces a new profile. Merging profiles is listed as future work.

## D8. Scope of what is profiled (2026-11-09)

**Decision.** Default channels: Say, Tell (incoming), Party, Shout, Yell, Linkshells, Cross-World Linkshells. Free Company, Alliance, Novice Network and PvP Team are mapped but off by default. Emotes (standard and custom), outgoing tells, the user's own messages, system messages and NPC dialogue are never profiled. Non-English messages are processed normally with the language recorded.

**Why.** These were the user's explicit choices.

## D9. Privacy defaults (2026-11-09)

**Decision.** Raw message text is not stored unless the user turns it on (default off, 30-day retention when on). Recent text for summaries is held in memory only and lost on unload. The UI warns when the Ollama endpoint is not a loopback address. Sender names are not sent to the model for classification. Per-character delete and delete-all are required features.

**Why.** The plugin handles other people's messages, including private tells.

## D10. LiteDB for storage (2026-11-09)

**Decision.** LiteDB behind `IProfileStore` and `IMessageLog`.

**Why.** It is pure managed code, so nothing native has to ship with the plugin. SQLite would need a native library.

## D11. Prompt-injection stance (2026-11-09)

**Decision.** Chat text is treated as untrusted data. Classification output is constrained by a JSON schema with enum values, and parsing is lenient (bad values become `Unknown`). Summaries are length-capped, cleaned of control characters, and rendered as plain text only.

**Why.** Any player can type text that looks like instructions to the model.

## D12. Topic cap raised from 50 to 200 (2026-11-09)

**Decision.** A profile tracks at most 200 topics; the UI shows the top 10.

**Why.** Found in the milestone 1 review. With a cap of 50, once every tracked topic had appeared twice, any new topic arrived with a count of 1 and was evicted immediately, so a long-lived profile could never pick up new interests. This was a flaw in the original spec, not in the implementation.

**Revisit if** topic lists still look stale in real use. The standard alternative is the Space-Saving algorithm, where a new item replaces the minimum and inherits its count.

## D13. Summary whitespace handling (2026-11-09)

**Decision.** In summaries, every run of control characters and whitespace becomes a single space.

**Why.** Found in the milestone 1 review. Deleting control characters also deleted line breaks, which glued words together.

## D14. Default model is `llama3.2:3b` (2026-11-09, provisional)

**Decision.** Small and fast default, changeable in settings.

**Why.** Chosen without measurement. Whether a 3B model labels game chat well enough is the biggest open question in the project.

**Revisit** in the model evaluation step planned before milestone 6 (see `docs/STATUS.md`).

## D15. Commands and UI are a first proposal (2026-11-09)

**Decision.** Commands are `/mmoprofiler` and `/mmop`. Profiles open by command or by right-clicking a name ("View Profile"). The three windows (profile list, profile detail, settings) in spec section 9.2 are a starting point.

**Why.** The user specified the two ways to open a profile but had not thought about the UI otherwise. Expect this to change before or during milestone 6.

## D16. Milestone 2 choices where the spec was silent (2026-11-09)

**Decision.**
- `ProfileService` takes the `PendingSummaryBuffer` and optional `IMessageLog` and exposes them as `Pending` and `MessageLog`; the engine uses those, so the engine constructor matches the spec.
- `PendingSummaryBuffer.Clear(key)` and `Remove(key)` both drop the character's entry; `ClearAll()` was added for `DeleteAllAsync`.
- `ApplyBatchAsync` returns `Task`, as in the spec; the engine re-reads touched profiles with `Get` to decide who needs a summary.
- `CircuitBreaker`: failures recorded while the breaker is already open are ignored, so one open period cannot double twice. `RecordSuccess` runs after each successful chunk or summary, not once per cycle.
- `MessageIntake` reads `QueueCapacity` once at construction; changing it needs a new instance. All other options are read on every call.
- `MessageIntake.Accept` does not call `Normalize()`; the host is expected to supply normalised options.
- `ProfilerEngine`: `AnalyzerAvailable` starts true and goes false on the first analyser or summariser failure or while the breaker is open. `Analyzed` counts messages whose result was not fully `Unknown`. The hourly retention purge runs only while `StoreRawMessages` is on, and first runs on the first cycle that logs.
- Analyser and summariser failures set `LastError` without ending the cycle; an exception from the batch save is a cycle failure that the loop catches.
- `SetNotesAsync` creates the profile if it does not exist, so the UI can take notes on a character with no profile yet.

**Why.** The spec left these open. Each is the smallest reading that satisfies the listed tests.

