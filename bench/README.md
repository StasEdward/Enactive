# Live benchmark (plan phase 0, amendment A — 0b)

Runs real tasks through the engine as it is configured (the console host reads the same `settings.json` as the
window: the planner and reviewer, the local worker), then judges each run by **what it left on disk** — never by the
engine's own word — and compares the engine's outcome with that truth.

## Scenarios

`bench/scenarios/<name>/`

- `scenario.json` — the request, `approve` (`allow` for scenarios that need a shell, `deny` for those that do not),
  the expected outcome, and the checks that are the truth: `file_exists`, `file_contains` (fixed patterns or
  `patternsFrom` a command run at check time), `unchanged` (against the fixture), `command` (exit code, text,
  a number read from the output) and `coverage` (how many of the known items a report mentions).
- `fixture/` — the project the task is asked of. Copied fresh into the temp folder for every run, put under git.
  Never touched in place.
- `solution/` — a reference solution laid over the fixture. The truth must hold on it; where it does not, the truth
  would call a right run wrong.

| Scenario | What it measures |
| --- | --- |
| `disk-report` | A task on the machine, no code; short enough to reach the step review and the final checks. |
| `test-coverage` | The coverage request on a small C# library: tests to add, a report to write. |
| `wiki-drift` | Wiki against code, the code in Python: four seeded discrepancies to find (coverage). |
| `build-error` | A C# project that does not build before the work: the work is elsewhere, the old error must not be held against it. |

## Running

```bash
dotnet run --project tools/Enactive.Bench -- --fixtures      # no model: every truth must FAIL on its fixture
dotnet run --project tools/Enactive.Bench -- --solutions     # no model: every truth must HOLD on its solution
dotnet run --project tools/Enactive.Bench                    # the live run, all scenarios, once each
dotnet run --project tools/Enactive.Bench -- --only wiki-drift --timeout 30
dotnet run --project tools/Enactive.Bench -- --save-baseline # this run becomes what later runs are compared with
```

The live run builds the console host into the temp folder, needs the configured providers up (the local model
server, the cloud planner/reviewer) and costs their tokens. Each run's folder under `bench/results/` holds
`summary.md`, `results.json` and, per scenario, the console log and the engine's report; only `baseline.json` is kept
in git. The exit code is 1 when any scenario is a false PASS or a false FAIL.

## Verdicts

| Truth \ Engine | outcome as expected | not |
| --- | --- | --- |
| holds | right PASS | **FALSE FAIL** |
| fails | **FALSE PASS** | right FAIL |
