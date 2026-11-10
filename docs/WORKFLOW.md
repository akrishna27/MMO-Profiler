# Workflow

How this project is built: a planning-and-review model (Opus) and an implementation model (Sonnet), with the user as product owner and tester.

## Who does what

| Role | Does | Does not |
|---|---|---|
| User | Decides scope, runs `dotnet test`, tests in game, pushes | Read every line of code |
| Sonnet session | Implements one milestone from the spec, writes its tests, commits | Start the next milestone, change the spec silently |
| Opus session | Plans, reviews each milestone, fixes spec flaws, evaluates models, debugs hard problems | Rubber-stamp a green test run |

Use a fresh session for each review. Do not review in the session that wrote the code.

## The loop

1. The user prompts Sonnet with the implementation prompt for milestone N.
2. Sonnet implements, runs the tests, commits, and lists deviations and ambiguities.
3. The user runs `dotnet test` themselves.
4. The user prompts Opus with the review prompt.
5. Opus reports. The user decides which findings to fix and who fixes them.
6. Fixes are committed with matching spec and doc updates. `docs/STATUS.md` is updated.
7. The user pushes, then starts milestone N+1.

## Implementation prompt (Sonnet)

```
Read CLAUDE.md and docs/SPEC.md in full. Implement milestone N only, following
the rules in section 2 of the spec. Write every test listed in section 11 that
belongs to this milestone. Run dotnet test and show me the full output. Commit
when everything passes, then stop. Do not start milestone N+1. Finish by
listing anything you changed from the spec or found ambiguous.
```

For milestone 6 (the Dalamud plugin), add:

```
Before writing code, read dalamud.dev and the current SamplePlugin, and list
every difference from section 9 of the spec.
```

## Review prompt (Opus)

```
Milestone N is committed. Review it against docs/SPEC.md before I start the
next one. Read every changed file. Check that each test named in section 11
for this milestone exists, and that no test is skipped or has weakened
assertions. Run the tests yourself. Report defects separately from minor
points, say whether each defect is the code's fault or the spec's, and do not
change anything until I say so.
```

## Review checklist

- **Scope.** `git diff --stat` against the previous milestone. Nothing from a later milestone, no build output, no new dependency outside spec section 3.
- **Spec match.** Read each changed source file next to its spec section. Note every difference, including sensible ones.
- **Tests exist.** Every name in section 11 for this milestone is present.
- **Tests are honest.** No `Skip`, no assertion that only checks "not null" or "at least 1" where an exact value is known, no test that asserts the current behaviour of a bug.
- **Tests run.** Run `dotnet test`; report passed, failed and skipped counts.
- **Spec flaws.** For each defect, ask whether the spec caused it. Two of the first two defects found were traced to the spec (one fully, one through ambiguity).
- **Project rules.** The ten rules in `CLAUDE.md`, especially: no Dalamud outside the plugin project, no account IDs, no I/O on the game thread, no network calls except Ollama.
- **Risk areas.** Give extra scrutiny to threading in the pipeline (milestone 2) and to every Dalamud API call (milestone 6). A second pass with `/code-review high` is worth it there.

## Planning prompt (any new feature or app)

```
Here is what I want to build and why: <goal>. Constraints: <list>.
Before doing anything, tell me what I've got wrong or haven't considered, and
ask me the questions whose answers would change the design. Give me a
recommendation for each choice, not a list of options. Mark anything you are
stating from memory rather than from a source you checked.
```

Then: agree the answers, write or extend the spec with named tests and gated milestones, record the decisions in `docs/DECISIONS.md`, and only then implement.

## Maintenance

- **Bug reports.** Reproduce with a failing test first, then fix. Commit both together.
- **Game patches.** Dalamud's API level changes with major game patches. Read the Dalamud changelog, list the breaking changes that touch `src/MmoProfiler.Dalamud`, then update. The core projects should not need changes.
- **Model changes.** Re-run the model evaluation (once it exists) before changing the default model or the prompts.

## In-game acceptance testing

The checklist is in spec section 12. Setup:

1. Build, then in game open `/xlsettings`, Experimental tab, and add the built `MmoProfiler.dll` under Dev Plugin Locations. Enable it in `/xlplugins`.
2. Keep `/xllog` open for errors.
3. Test somewhere quiet first with a friend or an alt, so the expected messages are known.
4. Then spend an hour in a busy city, watching for stutter when a batch runs. The model and the game share the GPU; try a smaller model if it stutters.
5. Disable and re-enable the plugin several times to check it unloads cleanly.

Third-party plugins are used at the player's own risk under the game's terms of service.
