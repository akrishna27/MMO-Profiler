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

## D17. Deletes win over in-flight work (2026-11-09)

**Decision.** A delete is never undone by a batch that was already being processed. Each engine cycle runs inside a `ProfileService` batch scope; deletes during the scope are recorded in it, and the batch save, the raw message log and summaries all skip deleted characters. Summary writes use `UpdateIfExistsAsync` and can never create a profile.

**Why.** Found in the milestone 2 review. The original spec did not say what a delete during a cycle should do, and the implementation recreated the profile (and, with raw storage on, logged the batch's messages) after the user had deleted it. For a tool that stores other people's chat, a delete that does not stick is not acceptable.

**Alternatives rejected.** A global counter with per-key tombstones grows without bound. Holding the mutation lock for the whole cycle would block the UI's note edits and deletes for as long as the model takes.

**Implementation note (2026-11-09).** The raw log write goes through `ProfileService.AppendToLogAsync`, which holds the mutation lock while it checks the scope, instead of the engine checking `IsDeleted` and then calling the log itself. That closes the gap between the check and the write.

## D18. Milestone 3 choices where the spec was silent (2026-11-09)

**Decision.**
- `OllamaClient` takes an optional third constructor argument, a `TimeProvider`, used only for the request timeout. Production passes nothing and gets the system clock.
- For a duplicated result id the parser keeps the first and ignores the rest.
- `CheckHealthAsync` never throws except for caller cancellation; any failure to get a usable `/api/tags` answer is `ServerUnreachable`. An untagged model name matches `name:latest`.
- Line breaks in chat text are flattened to spaces in the summary prompt, so a player cannot forge `[Channel]` lines.
- Chat text in the classification prompt is serialised without escaping non-ASCII characters, so non-English text stays readable to the model.

**Why.** The spec was silent, or in tension with rule 8 (timers need a `TimeProvider`). Each is the smallest reading that keeps the listed tests meaningful.

## D19. Set the context window explicitly and budget prompts by characters (2026-11-09)

**Decision.** Every Ollama request sets `options.num_ctx` from `OllamaContextTokens` (default 8192). Classification chunks close at 20 messages or 4000 characters of text, whichever comes first. Summary input is limited to the newest messages within 6000 characters.

**Why.** Measured in the smoke test against Ollama 0.40.15 with `llama3.2:3b` on an RTX 4070 (12 GB). The server chose a default window of 4096 tokens from the machine's VRAM ("vram-based default context"), so machines with less VRAM may get less. A 6054-token prompt was cut to 2050 tokens, the start of the text was lost, and the reply was still HTTP 200 with `done_reason: stop`; the only trace was a warning in the server log. A full chunk of 20 messages at 500 characters each fitted in 4096 tokens with English text, but text that tokenises badly (Japanese is roughly one token per character) would not.

**Sizing.** Worst case for classification is about 4000 prompt tokens of chat text plus about 400 for the instructions and schema and about 800 for the reply, which fits in 8192. Typical chat (around 60 characters a message) is unaffected by the character budget.

**Revisit if** the model evaluation picks a model with a smaller trained context, or if 8192 costs too much VRAM alongside the game.

## D20. LiteDB storage choices where the spec was silent (2026-11-09)

**Decision.**
- Package: `LiteDB` 5.0.21, the latest 5.x, as allowed by spec section 3.
- Documents are built by hand as `BsonDocument` instead of using LiteDB's object mapper, so enum keys are always written as names, unknown names are dropped on read, and nothing depends on mapper conventions.
- `DateTimeOffset` is stored as the UTC tick count (`long`). LiteDB's own `DateTime` handling converts to local time on read; ticks avoid that and sort correctly in the index.
- A profile document carries `gameId`, `name`, `realm` and `nameLower` next to the `_id`. `List(nameContains)` filters in code with a plain substring test, so `%` and `_` typed into the search box are literal.
- A document whose `schemaVersion` is higher than 1 is refused with `NotSupportedException` instead of being read as version 1. `Migrate` is a private method in `LiteDbProfileStore`; missing version means 1.
- `LiteDbStorage` owns the database and exposes `Profiles` and `Messages`; disposing it closes the file. `OpenInMemory()` uses a `MemoryStream`.
- `GetRecent` orders by timestamp, then by insertion id, newest first.

**Why.** The spec fixed the collections and indexes but not the encoding. Each choice is the smallest one that makes the listed storage behaviours hold, and the version refusal protects a user who downgrades the plugin from silently corrupting data.

## D21. Topic text is cleaned before it becomes a storage key (2026-11-09)

**Decision.** `ProfileUpdater.ApplyAnalysis` replaces control characters and whitespace runs in topics with a single space. As a second line of defence, the LiteDB store skips any `LanguageCounts` or `TopicCounts` key containing a NUL character when writing.

**Why.** Found in the milestone 4 review by probing the real store. Topics come from the model, and so indirectly from what other players type. They are used as LiteDB field names. LiteDB throws `LiteException` ("Invalid null character") for a field name containing NUL, and because the engine saves a cycle with one `UpsertMany`, a single such topic made the store save none of that cycle's profiles. Other awkward keys (spaces, `$`, `.`, quotes, `_id`, Japanese, tabs, line breaks) round-trip correctly, and NUL inside values (notes, summary, message text) is fine. The gap was in the original spec, which did not clean topics, combined with D20's choice to use topics as field names; neither is wrong on its own.

## D22. FFXIV chat codes verified; small readings for milestone 5 (2026-11-09)

**Decision.** The section 9.1 chat-type table needed no correction. Every code and Dalamud name was compared with `Dalamud/Game/Text/XivChatType.cs` on the `master` branch of goatcorp/Dalamud (fetched 2026-11-09): Say 10, Shout 11, TellOutgoing 12, TellIncoming 13, Party 14, Alliance 15, Ls1 to Ls8 16 to 23, FreeCompany 24, NoviceNetwork 27, CustomEmote 28, StandardEmote 29, Yell 30, CrossParty 32, PvPTeam 36, CrossLinkShell1 37, CrossLinkShell2 to 8 101 to 107. All match. This was checked against `master`, not against the API 15 tag specifically.

Readings where the spec was silent:
- `CleanSenderName` uses `char.IsLetter`, not ASCII only, and returns null for null input. FFXIV names are Latin only, so this is slightly more lenient than the game; the cost is nil and it avoids dropping a sender if the rules widen.
- `ProfileView` ties: tone and intent by enum order, topics alphabetical (ordinal), busiest hours earliest in the week first. `MostActive` is in local time, matching the heat map. A half-hour offset rounds away from zero (+5:30 becomes +6, -3:30 becomes -4).
- `ProfileView` returns `HeatMap` as `int[7, 24]` with day 0 = Sunday, and `MostActive` as a list of strings.
- `ProfileView` carries identity and counts but not channel or language breakdowns, which the spec's list for 9.3 did not include.

**Why.** Each is the smallest reading that makes the listed tests meaningful. The code check was requested because the table was written from memory.

## D23. The 20-character name limit does not count the space (2026-11-09)

**Decision.** `FfxivNames.CleanSenderName` accepts a name when forename plus surname total at most 20 characters, not counting the space between them.

**Why.** Found in the milestone 5 review. The original spec said "20 characters maximum in total" and the implementation counted the space, so a name of 10 + 10 letters was rejected and that player would never be profiled. The game's rule, as quoted from the Console Games Wiki character creation page on 2026-11-09, is "Forenames and surnames are restricted to 15 characters or less, with a combined limit of 20 characters." That page does not say whether the space counts; the belief that it does not is from memory and unverified. The choice is deliberately the lenient one: this check exists to reject garbage, so accepting one character too many costs nothing, while rejecting a valid name silently drops a real player.
