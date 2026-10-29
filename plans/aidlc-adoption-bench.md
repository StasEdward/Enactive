# AIDLC adoption - bench measurements for Phases 1-3

Measured 2026-10-17/18 with `tools/Enactive.Bench`, as the plan's "Measurement" sections ask. "Before" is `master`
@658db8c (the branch's merge base), "after" is `feat/aidlc-adoption` @28eb407. Raw results: `bench/results/<stamp>/`
(not in git; stamps below).

## What these numbers can and cannot say

- **One local model in every role.** Plan, review, execute: `gemma-4-26B-A4B-it-UD-Q3_K_M` on llama.cpp (reasoning
  on, budget 8192, KV cache q8_0, temperature 0 from the engine). The plan's design assumes a strong planner and
  reviewer; the baseline in `bench/results/baseline.json` (2026-10-01) was taken with one. A good share of the failures
  below are the local planner/reviewer failing at JSON or at judging, on both branches alike. Read the numbers as "the
  engine with a weak model everywhere", and repeat with a strong planner/reviewer before turning anything on by default.
- **Few runs.** Five per side for Phase 1, one per limit for Phase 2, two valid raised runs per scenario for Phase 3.
  Differences of one run are noise.
- **No Python on the machine.** `missing-input` expects a script to make its input; the worker worked around it by hand.
- **`disk-report` truth is stricter than the task.** Every run on every setting wrote a correct table naming the
  volumes `C` and `D`; the truth wants `C:`, so all are scored FALSE PASS. That is the scenario, not the engine - it
  should accept a bare letter in a volume column.

## Phase 1 - derived coverage (`wiki-drift`, `test-coverage`, `StepOutputs` + `TypedCriteria` on, 5 runs each)

| Scenario | Side | Green runs | right PASS / right FAIL / FALSE PASS / FALSE FAIL | Coverage of green runs | Avg s | Avg tokens in / out |
| --- | --- | ---: | --- | --- | ---: | --- |
| wiki-drift | before | 4 | 3 / 1 / **1** / 0 | 4/4, 4/4, 4/4, **3/4** | 175 | 143k / 19k |
| wiki-drift | after | 1 | 1 / 2 / **0** / **2** | 4/4 | 224 | 198k / 24k |
| test-coverage | before | 0 | 0 / 5 / 0 / 0 | - | 297 | 406k / 27k |
| test-coverage | after | 1 | 1 / 4 / 0 / 0 | - | 402 | 599k / 36k |

- **The goal is met: no run green with uncovered items after the change.** Before, one of five `wiki-drift` runs ended
  Completed with 3 of 4 discrepancies found - the hole Phase 1 was built to close. After, none did.
- **Cost:** about +40% tokens in and +30% time on `wiki-drift`, +50% tokens in on `test-coverage`.
- **But the gate costs right runs: two FALSE FAILs on `wiki-drift`, both with 4/4 found.** The cause is one engine
  defect, seen in all five "after" runs:
  - Step 2 consumes step 1's items (`Docs/wiki/CLI.md`, `Docs/wiki/Configuration.md`) and must key its results by them.
    The local worker keys them by something else first, every time: the bare name (`CLI.md`), the path in quotes
    (`'Docs/wiki/CLI.md'`), or one summary key (`discrepancies`).
  - `EvidenceCoverage.Gather` takes each key as a path to look up in the read ledger, so the answer
    (`EvidenceCoverage.Unbacked`) is "CLI.md (never read). Read or check them, then hand the result on again" - although
    the step had read `Docs/wiki/CLI.md` whole. The advice is wrong: the files were read; the KEY is not one of the
    items. The worker re-reads and resubmits the same keys until the repeat guard stops the step (run
    `20261018-001423`), or the reply runs away (`20261018-002828`).
  - When the step-end coverage check rejects instead (it names the items: "not covered: Docs/wiki/CLI.md (no
    result)"), the worker corrects the keys and the run passes (`20261017-235713`).
  - Fix, general: when a results key is not one of the consumed items, say so and list the keys expected, instead of
    "never read"; and resolve a key that names exactly one consumed item unambiguously (its file name, or the path in
    quotes) to that item. A follow-up with its own test.
- `test-coverage` fails alike on both sides: in 6 of the 9 failed runs the reviewer rejected the "analyze coverage"
  step because the coverage output it cited was truncated or unread; in 2 the worker repeated `list_dir`/`dotnet` calls.
  The one green "after" run is right.
- One "after" `test-coverage` run (`20261017-231237`) was rejected by the coverage gate with "Shelf/Inventory.cs (no
  file_write evidence)": the local planner made the library file the item of the "add tests" step with write evidence,
  while the right work writes a test file and must leave `Inventory.cs` unchanged. A planner error the gate enforced; worth
  a look at whether plan-time classification should refuse write evidence on an item the request forbids changing.

Stamps: before `20261017-230735`, `20261017-232548`, `20261017-234739`, `20261018-000538`, `20261018-002256`
(the FALSE PASS); after `20261017-231237`, `20261017-233718`, `20261017-235713`, `20261018-001423`, `20261018-002828`.

## Phase 2 - obligation loop (`missing-input`, `setup-step`, `disk-report`, limits 1, 2, 3, one run each)

| Limit | missing-input | setup-step | disk-report | Completed | Tokens in (sum) |
| ---: | --- | --- | --- | ---: | ---: |
| 1 | right PASS | right PASS | Completed (scored FALSE PASS, see above) | 3/3 | 815k |
| 2 | right PASS | right PASS | Completed (same) | 3/3 | 639k |
| 3 | right FAIL (0/3 - judged the input "not reachable") | right PASS | Completed (same) | 2/3 | 656k |

- The loop barely fires on these scenarios: at limits 2 and 3, one or two releases per run ("Not told again of the calls
  still open: ... nothing the engine can see has changed since"), none at limit 1. The one failure at limit 3 is the
  local planner declaring the missing input unreachable, not a nudge loop.
- **No evidence for raising the default above 1.** Higher limits did not finish more runs or spend fewer tokens.
  Keep `PerKind = 1`; answer open question 2 with the bench on a strong planner/reviewer, on a scenario where calls
  stay open.

Stamps: limit 1 `20261017-223926`, limit 2 `20261018-004059`, limit 3 `20261018-005146`.

## Phase 3 - `build` / `test` raised to blocking at step level (`build-error`, `test-coverage`)

Global manifests `{"id":"build","fireOn":"Step","severity":"Blocking"}` and the same for `test`, in
`%APPDATA%/Enactive/checks` for the runs only.

| Scenario | Default (limit-1 series) | Raised, run 1 | Raised, run 2 |
| --- | --- | --- | --- |
| test-coverage | Incomplete, FALSE FAIL (review unusable) | Completed, right PASS | Failed, right FAIL |
| build-error | Incomplete, FALSE FAIL (check diagnosis JSON) | Incomplete, **FALSE FAIL** | Incomplete, right FAIL |

- **test-coverage:** `build` and `test` ran after steps 2 and 3, passed, and decided nothing wrongly. The failed run
  failed on its report's unverified coverage claim.
- **build-error: raising `build` to blocking is wrong for a workspace that does not build before the work.** The
  scenario's broken file is elsewhere; the step adds `Median` correctly. The raised check compares only the exit code,
  so it fails step 1 for the old error ("the engine's check 'build' failed (exit 1)") while the engine's own
  "No new build errors" (against the run's baseline) passes the same state. Told its step failed, the worker goes after
  the old error - `Ledger.csproj`, `Report.cs` - which the request forbids ("Остальной код не трогай."); the write
  boundary refuses the `edit_file` calls and the step stops on repeated calls. Both raised runs, same pattern - and in
  the second (`20261018-012348`), after two refused `edit_file` calls, the worker rewrote `Report.cs` through
  `run_command` (`powershell ... -replace ... | Set-Content`), which the write boundary does not see. The step reviewer
  caught it ("violates the instruction 'Остальной код не трогай'"), but the run ended with `Report.cs` still changed,
  so the truth (`broken-file-left-alone`, `same-one-error`) rightly fails. The raised check did not just cost a run; it
  pushed the worker to break a stated restriction. Separately worth checking: why that shell-made change was left in
  place after the step was rejected (`RevertRejectedSteps` is on).
  - This is the reason the code gives for keeping the raise opt-in (`CheckStore`, "a new build error at the end does
    not prove this run made it"); the bench confirms it. Improvement worth a follow-up: a blocking step check that
    already failed at the step's start should say so to the worker ("it failed before this step too - the step is not
    asked to fix that") or be judged against that start, as `BuildRegression` is.
- The manifest guard works: the first raised series used `fire_on` (my typo); the engine named the unknown field,
  skipped both manifests and said so at run start (`20261018-010201`, not counted above). `test.json` in `build-error`
  (no test project) was skipped with "no check of that id below it to change", which is right.

Stamps: raised `20261018-011302`, `20261018-012348`.

## Phase 1.5 - flags off vs on with a strong planner/reviewer (2026-10-28)

The second measurement Phase 1.5 asked for, after the results-key fix (`01b40ea`). `master` @0731c5c for both sides;
the only difference is `Engine.StepOutputs` + `Engine.TypedCriteria` in `settings.json`. Plan and review:
`Anthropic/claude-sonnet-5-5`; execute: `llama.cpp/Qwen3.6-35B-A3B-Q4_K_M` (the other roles' gemma binding was not
loaded on the server). **One run per side** - read it as a direction, not a result.

| Scenario | Side | Verdict | Steps | Reviews ✓/✗ | s | Tokens in / out | Plan + review tokens in | Coverage |
| --- | --- | --- | ---: | --- | ---: | --- | ---: | --- |
| wiki-drift | off | right PASS | 4 | 4/0 | 77 | 105k / 6k | 23k | 4/4 |
| wiki-drift | on | right PASS | 2 | 2/0 | 46 | 71k / 3k | 16k | 4/4 |
| test-coverage | off | right PASS | 3 | 3/0 | 208 | 558k / 9k | 68k | - |
| test-coverage | on | right FAIL | 3 | 2/2 | 201 | 576k / 11k | 128k | - |

- **No FALSE PASS and no FALSE FAIL on either side.** The results-key defect that cost the 2026-10-18 "after" runs did
  not show: on `wiki-drift` the step handed its results on at once and the run passed with 4/4.
- **`wiki-drift` is cheaper with the flags on:** half the steps, about a third fewer tokens in and less time.
- **The `test-coverage` failure is the worker's, not the flags'.** The report step wrote the "before" branch coverage
  of methods as 1/6 where the Cobertura output it had read says 3/6; the reviewer rejected it twice, naming the row
  and the right value, and the rejected report was put back (`RevertRejectedSteps`), so no `.md` was left. The flags
  did what they are for: the step handed its report on with `submit_step_output`, and the planner added
  `file_exists <step 3's report>` as a typed criterion. The engine's own word (Failed) was right.
- **Environment:** the C# fixtures target `net8.0` and this machine has only .NET 10/11, so a plain `dotnet test`
  does not start the tests. In the first `test-coverage` run (`20261028-220204`, not counted above) the run itself was
  right: step 1's review rejected a coverage "measurement" read from the source and pointed at
  `DOTNET_ROLL_FORWARD=Major`; the worker ran the tests with it from then on (20 passed, 0 failed, 16/16 lines), and
  each review cited those calls. Only the checks run without it failed to start the tests - the engine's final
  regression check (NOT CHECKED, which holds nothing back, rightly) and the bench's truth, which turned down the
  reference solution as well. So the FALSE PASS was the bench's environment, not the engine's. Rerun with
  `DOTNET_ROLL_FORWARD=Major`; `--fixtures`/`--solutions` agree under it. Follow-up, since done (PR #63): the bench
  judges the solution before a live run and scores a scenario whose truth turns it down NOT RUN.

Stamps: off `20261028-220204` (`wiki-drift`), `20261028-220935` (`test-coverage`); on `20261028-221352`.

**Two more runs a side, 2026-10-29** (`master` @24348b7, same models, `DOTNET_ROLL_FORWARD=Major`) - three a side with
the runs above:

| Scenario | Side | Verdicts | Steps | s | Tokens in |
| --- | --- | --- | --- | --- | --- |
| wiki-drift | off | right PASS ×3 (4/4 each) | 4, 5, 4 | 77, 108, 86 | 105k, 159k, 128k |
| wiki-drift | on | right PASS ×3 (4/4 each) | 2, 2, 2 | 46, 50, 51 | 71k, 80k, 80k |
| test-coverage | off | right PASS ×1, right FAIL ×2 | 3, 4, 4 | 208, 206, 94 | 558k, 881k, 121k |
| test-coverage | on | right PASS ×2, right FAIL ×1 | 3, 3, 3 | 201, 175, 200 | 576k, 472k, 486k |

- No false verdict on either side in six runs each.
- `wiki-drift` with the flags on: half the steps, about half the time, a third to a half fewer tokens, every run.
- Every `test-coverage` failure is a worker's mistake a strict review caught: off, step 1 ("measure coverage") was
  rejected twice - once for switching the project to `coverlet.msbuild`, once for a wrong account of an uncovered
  branch - and with `ReviewRetries = 1` the step failed and the three after it were skipped; on, the one failure is the
  report's wrong "before" number (above). One wrong side claim failing the whole run is worth a look of its own.
- Decision: `StepOutputs` + `TypedCriteria` on by default (schema 10 reads an older file's written-out off as on),
  with a box for each in both settings pages. `DynamicSteps` stays off - not measured.

Stamps: off `20261029-112041`, `20261029-112618`; on `20261029-112955`, `20261029-113358`.

## Follow-up 3 - a command's change held to the request's limit (2026-10-28)

Built on `feat/shell-change-limit` (`8ec8d48`..`38e579e`). Same setup as Phase 1.5 above (Sonnet 5.5 planning and
reviewing, local Qwen3.6 worker, `DOTNET_ROLL_FORWARD=Major`). Measured at `539c734`, before the fixes the branch's
review asked for (per-file answers, the run's start read once, the refusal kept in a test run's reply, a cap on the
question, an unmeasured call said); those change only what happens once the check fires, and it did not fire here.

**`build-error` with `build` raised to blocking at step level** (the 2026-10-18 setup that drove the worker to the
shell), three runs: right PASS each (56, 73, 58 s; 66k, 130k, 75k tokens in). In each the change limit refused one
`edit_file` to `Report.cs`, and the worker this time did not go round it: its commands (`dotnet build`, `findstr`,
`csc`, a PowerShell read of `Stats.cs`) changed no file the run found, so the new check was not asked once and
`Report.cs` was left as it was. **The measurement shows no false refusal of a build; it does not reproduce the
2026-10-18 shell rewrite** - that behaviour is the worker's and did not recur in three runs. The behaviour itself is
pinned by `WhatACommandChangedIsHeldToTheLimitTests` (a real `run_command` rewriting the forbidden file, put back
byte for byte).

**One ordinary run of every scenario** (`20261028-232821`): build-error, disk-report, missing-input, setup-step and
test-coverage right PASS; wiki-drift a FALSE FAIL with 4/4 found - its last step's review rejected `DRIFT.md` for wrong
line numbers the report claimed were right, the worker's error. The new check was asked in no scenario. Tokens are
within the spread of the runs above.

Stamps: raised `20261028-232426`, `20261028-232532`, `20261028-232652`; all scenarios `20261028-232821`.

## Answers for the plan

1. Phase 1.5 - do **not** turn `StepOutputs` + `TypedCriteria` on by default yet: the hole is closed, but the results
   key defect turns right runs into failures. Fix it, then measure again (with a strong planner/reviewer as well).
   *2026-10-28:* measured again with a strong planner/reviewer, one run per side: no false verdicts with the flags
   on, `wiki-drift` cheaper, the one failure a worker's wrong number. In favour of turning them on, not yet enough to.
   *2026-10-29:* three runs a side - turned on by default.
2. Phase 2 - keep the per-kind limit at 1; no measured gain from 2 or 3.
3. Phase 3 - the raise works as built and must stay opt-in; the pre-existing-failure wording is the next improvement.
4. Scenario fixes: `disk-report` should accept `C` as well as `C:`.
