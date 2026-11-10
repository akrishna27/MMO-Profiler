# MMO-Profiler

A chat profiler for MMOs, first targeting Final Fantasy XIV as a Dalamud plugin. It reads incoming chat, has a local model (Ollama) label tone and intent, and builds a local profile per character.

## Read these first

| File | What it is |
|---|---|
| [docs/STATUS.md](docs/STATUS.md) | Where the project stands right now and what comes next. Start here. |
| [docs/SPEC.md](docs/SPEC.md) | The source of truth for behaviour, architecture, tests and milestones. |
| [docs/DECISIONS.md](docs/DECISIONS.md) | What was decided and why. Do not reopen a decision without a new reason. |
| [docs/WORKFLOW.md](docs/WORKFLOW.md) | How the work is split between models, with the prompts and the review checklist. |

## Rules

1. **The spec is the source of truth.** If code and spec disagree, find out which is wrong and fix that one. Any change in behaviour updates `docs/SPEC.md` in the same commit.
2. **Keep the docs current.** Every change to the codebase also updates `docs/STATUS.md`, and `docs/DECISIONS.md` when a decision is made or reversed. A session that leaves these stale has not finished.
3. **One milestone per session, then review.** Do not start the next milestone until the current one has been reviewed (see `docs/WORKFLOW.md`).
4. **Only `MmoProfiler.Dalamud` may reference Dalamud.** Core, storage and the FFXIV mapping project must stay game-host-agnostic and unit-testable.
5. **Never collect account IDs or content IDs** of other players. A character is a name plus a home world, nothing else.
6. **Nothing leaves the machine** except chat text sent to the user's configured Ollama endpoint. No telemetry, no other network calls.
7. **The chat callback does constant-time work only.** No I/O, no awaiting, no locks held during I/O on the game thread.
8. **Verify Dalamud APIs against current docs** (<https://dalamud.dev>, <https://github.com/goatcorp/SamplePlugin>) before using them. The Dalamud details in the spec were written from memory of older API levels.
9. **Run the tests before saying something is done**, and report the real output. Never skip, delete or weaken a test to get a pass.
10. Say plainly when something is stated from memory and not checked.

## Commands

```bash
dotnet build
dotnet test
```

Requirements: .NET 10 SDK, Git. For the plugin: XIVLauncher with Dalamud, and Ollama with the default model (`ollama pull llama3.2:3b`).

## Conventions

- C#, nullable enabled, file-scoped namespaces, warnings as errors (see `Directory.Build.props`).
- `System.Text.Json` for JSON. Inject `TimeProvider`; never call `DateTime.Now` or `DateTimeOffset.UtcNow` in core code.
- xUnit with hand-written fakes. No mocking library. No test touches the network, the game or a real Ollama server.
- Allowed packages are listed in section 3 of the spec. Adding one needs a stated reason and an entry in `docs/DECISIONS.md`.
- Commits go to `main`, one per milestone or fix, with a message that says what changed and why. Push only when the user asks.

## The user

The user is the product owner and tester. They run the game, do the in-game acceptance testing, and decide scope. They are not reading every line of code, so reports must be accurate and lead with what matters: what works, what does not, and what they need to decide.
