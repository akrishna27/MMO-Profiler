# Status

Last updated: 2026-11-09 (milestone 2 reviewed and closed). Update this file with every change to the codebase.

## Where things stand

| Milestone | State | Notes |
|---|---|---|
| 1. Solution, core domain, `ProfileUpdater`, options | Done, reviewed, pushed | Commits `3b40392`, `a09de8e`. 56 tests pass. |
| 2. Intake, circuit breaker, summary buffer, profile service, engine | Done, reviewed | Commits `23b811b` and `61e90cd` (review fixes). 107 tests pass on 15 consecutive runs. |
| 3. Ollama client and response parser | Not started | |
| 4. LiteDB storage | Not started | |
| 5. FFXIV mapping and `ProfileView` | Not started | |
| 6. Dalamud plugin and windows | Not started | Blocked on the two items under "Before milestone 6". |
| 7. README and CI | Not started | CI should be pulled forward; see below. |

GitHub: <https://github.com/akrishna27/MMO-Profiler>, branch `main`.

## Next actions

1. Start milestone 3 (Ollama client and response parser) with the implementation prompt in `docs/WORKFLOW.md`.
2. Add the GitHub Actions workflow (spec section 13) now instead of waiting for milestone 7, so every push is tested.
3. Continue with milestones 3, 4 and 5. None of them needs the game.

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
- `Normalize_ClampsOutOfRangeValues` checks several values with "at least 1" instead of the exact bound. Tighten when next touching `ProfilerOptionsTests`.
- `FirstSeenUtc` and `LastSeenUtc` keep the offset the message carried. They compare correctly. The storage layer must normalise to UTC in milestone 4, as the spec says.
- `CharacterKey` has a public constructor that skips the trimming done by `CharacterKey.Create`. Storage will need the constructor; always use `Create` in application code.
- After a restart the in-memory summary buffer is empty but `MessagesSinceSummary` is saved, so the first summary can be written from one new message plus the old summary. Consider a minimum buffered-message count.
- For milestone 6: `MessageIntake.Accept` calls the options function on every chat message, so the plugin must return a cached normalised snapshot, not rebuild it per call. A profile created only by `SetNotesAsync` has `MessageCount` 0 and a default `FirstSeenUtc`; the UI must show that as "not seen yet". Dalamud's `Dispose` is synchronous and will block up to 2 seconds on the engine.
- `Directory.Build.props` turns on warnings-as-errors for every project. The spec only requires it for the non-Dalamud projects, so the plugin project may opt out if Dalamud's own warnings get in the way.

## Environment notes

- The development machine is Windows 10 with Git, the .NET 10 SDK, and XIVLauncher with Dalamud installed.
- Ollama must be installed by the user before milestone 3 can be tried against a real model, and before the model evaluation.
- The first `git push` on a machine needs a browser sign-in that an agent's shell cannot show. If a push fails with "terminal prompts disabled", the user runs it once themselves.

## After v1

See spec section 14: more channel toggles, a custom repository manifest and release workflow, a second analyser backend, profile export and merging, and a World of Warcraft host.
