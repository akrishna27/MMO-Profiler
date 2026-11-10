# Status

Last updated: 2026-11-09 (milestones 1 to 4 reviewed and pushed). Update this file with every change to the codebase.

## Where things stand

| Milestone | State | Notes |
|---|---|---|
| 1. Solution, core domain, `ProfileUpdater`, options | Done, reviewed, pushed | Commits `3b40392`, `a09de8e`. 56 tests pass. |
| 2. Intake, circuit breaker, summary buffer, profile service, engine | Done, reviewed, pushed | Commits `23b811b` and `61e90cd` (review fixes). |
| 3. Ollama client and response parser | Done, reviewed, pushed | Commits `53972be` and `c7e6b66` (context-window fix, D19). 167 tests pass on 10 consecutive runs. Verified against a real Ollama server. |
| 4. LiteDB storage | Done, reviewed, pushed | Commits `7720f68` and `8d092c8` (NUL fix, D21). LiteDB 5.0.21. 190 tests pass on 10 consecutive runs. D20 reviewed and accepted. |
| 5. FFXIV mapping and `ProfileView` | Not started | |
| 6. Dalamud plugin and windows | Not started | Blocked on the two items under "Before milestone 6". |
| 7. README and CI | Not started | CI should be pulled forward; see below. |

GitHub: <https://github.com/akrishna27/MMO-Profiler>, branch `main`.

## Next actions

1. Start milestone 5 (FFXIV mapping and `ProfileView`) with the implementation prompt in `docs/WORKFLOW.md`. It does not need the game.
2. Add the GitHub Actions workflow (spec section 13) now instead of waiting for milestone 7, so every push is tested.

## Ollama smoke test results

Run 2026-11-09 through the real `OllamaClient` against Ollama 0.40.15, `llama3.2:3b`, RTX 4070 12 GB, model fully on the GPU. The harness was a throwaway program outside the repo. Ollama is installed on the development machine at `D:\Ollama`; the server is not left running.

- **The client works end to end.** Health check, classification with the JSON schema, and summaries all succeeded. No reply failed to parse.
- **Silent truncation is real** (decision D19). The server's default context was 4096 tokens. A prompt over that was cut to about 2050 tokens with HTTP 200 and no error. Fixed in `c7e6b66` and re-checked: with `num_ctx` 8192, a worst-case Japanese classification chunk (8 messages, 4000 characters) and a Japanese summary request (30 messages of 500 characters, cut by the client to the newest 6000 characters, 4575 prompt tokens) both ran with no truncation warning in the server log. The loaded model grew from 2.6 GB to 3.1 GB of VRAM.
- **Speed.** First call 12.6 s (model load). Warm: 8 messages 3.6 s; 20 messages 6.5 to 7.9 s. That is roughly 3 messages a second, so a full cycle of 200 messages takes about 70 s. Summaries took under 1 s.
- **Label quality is weak.** On 8 realistic messages, intent was clearly wrong on 3: "LF2M healer and tank" was labelled Recruiting, a joke about a wipe was labelled LookingForGroup, and an insult was labelled Trading. Tone was mostly reasonable and German was detected, but in the re-check Japanese messages were labelled `en`, so language detection is unreliable too. The sample is tiny; this is what the model evaluation must measure. The prompt gives no definitions of the intents, which is the first thing to try.
- **Injection.** A message telling the model to label everything Friendly did not change the labels of the other messages.
- **Summary.** 58 words, plain text, no thinking text (this model is not a reasoning model). It contained one factual slip (selling gear described as seeking gear).
- **Not measured:** the effect on game frame rate while a batch runs. That needs the game.

## Before milestone 6

- **Model evaluation.** Build a small labelled set of game-chat messages, score two or three small Ollama models on tone and intent, and tune the prompts in spec section 7. Then confirm or change the default model (decision D14).
- **Dalamud API check.** Spec section 9 was written from memory of older API levels. Verify the chat event signature, the chat type codes, the context menu API, the local-player service and the SDK version against API level 15.
- **UI.** The windows in spec section 9.2 are a first proposal the user has not reviewed in detail (decision D15).

## Carried over from reviews

Minor points that were noted and deliberately left:

- A profile deleted and then recreated by `SetNotesAsync` while its summary call is still in flight would receive the old summary, because `UpdateIfExistsAsync` does not check the batch scope. The window is a few seconds for one profile. Close it by checking the scope under the lock if `ProfileService` is next touched.
- Messages still waiting in the intake queue when a profile is deleted are processed by the next cycle and recreate it, as if the character had just spoken. Accepted for now; Delete All could also empty the queue.
- If `DisposeAsync` gives up on a stuck cycle after 2 seconds, that cycle can still finish later and write to the store. In milestone 6 the plugin must tolerate a store call after the database is closed.
- `Dispose_InFlightCycleIgnoringCancellation_ReturnsAfterTwoSeconds` asserts the wait is under 3 fake seconds while stepping fake time against real scheduling. It has not failed, but if it ever flakes on a loaded machine, widen that upper bound.
- For milestone 6: `HttpClient.Timeout` defaults to 100 seconds, which silently caps any larger `OllamaTimeout`. The plugin should create the client with `Timeout = Timeout.InfiniteTimeSpan` and let `OllamaClient` enforce the timeout.
- `CheckHealthAsync` uses the full `OllamaTimeout` (60 seconds by default), so Test Connection against an unreachable remote host can take that long. A short fixed timeout for the health check would be friendlier.
- A non-2xx reply from `/api/chat` reports only the status code. Including Ollama's `error` text (for example "model not found") would make the status line more useful.
- If the summariser returns an empty reply, the engine still clears that character's buffered messages although no summary was written.
- `Normalize_ClampsOutOfRangeValues` checks several older values (queue depth, per-cycle limits, message length, retention, timeout) with inequalities instead of the exact bound. The three new options are checked exactly. Tighten the rest when convenient.
- `FirstSeenUtc` and `LastSeenUtc` keep the offset the message carried in memory. They compare correctly. Storage normalises them to UTC (done in milestone 4), so values read back always have offset zero.
- `CharacterKey` has a public constructor that skips the trimming done by `CharacterKey.Create`. Storage uses it to rebuild keys from values it wrote itself; always use `Create` in application code.
- After a restart the in-memory summary buffer is empty but `MessagesSinceSummary` is saved, so the first summary can be written from one new message plus the old summary. Consider a minimum buffered-message count.
- For milestone 6: `MessageIntake.Accept` calls the options function on every chat message, so the plugin must return a cached normalised snapshot, not rebuild it per call. A profile created only by `SetNotesAsync` has `MessageCount` 0 and a default `FirstSeenUtc`; the UI must show that as "not seen yet". Dalamud's `Dispose` is synchronous and will block up to 2 seconds on the engine.
- `Directory.Build.props` turns on warnings-as-errors for every project. The spec only requires it for the non-Dalamud projects, so the plugin project may opt out if Dalamud's own warnings get in the way.

- Milestone 4: `LiteDbMessageLog.GetRecent` loads every logged message for the character and sorts in memory. Bounded by the retention window, fine for v1; revisit if a busy character accumulates tens of thousands of rows.
- Milestone 4: `LiteDbProfileStore.List` with a name filter scans all profiles in `LastSeenUtc` order and filters in code. Fine for thousands of profiles.
- Milestone 4: a profile written by a newer schema version makes `Get` and `List` throw `NotSupportedException`. The plugin (milestone 6) must catch that and show a message rather than crash.
- For milestone 6: the database is opened in direct mode, which takes an exclusive lock on the file. `LiteDbStorage.Open` will throw if the file is still held, for example by a second game client or by a cycle that outlived a plugin reload. The plugin must catch that and show a message.
- Milestone 4: a single profile with a newer schema version makes `List` throw for the whole page, not just that row.
- Milestone 4: the database uses LiteDB's default collation, which is believed (from memory, not checked) to be culture-based and case-insensitive. Ids are already lower-cased, so this is harmless for FFXIV names; consider a fixed collation before supporting games with non-ASCII names.
- Milestone 4: the `profiles` and `messages` collections are not wrapped in a shared transaction, so a delete of a profile and its messages is two separate writes (as `ProfileService` already does).

## Environment notes

- The development machine is Windows 10 with Git, the .NET 10 SDK, and XIVLauncher with Dalamud installed.
- Ollama must be installed by the user before milestone 3 can be tried against a real model, and before the model evaluation.
- The first `git push` on a machine needs a browser sign-in that an agent's shell cannot show. If a push fails with "terminal prompts disabled", the user runs it once themselves.

## After v1

See spec section 14: more channel toggles, a custom repository manifest and release workflow, a second analyser backend, profile export and merging, and a World of Warcraft host.
