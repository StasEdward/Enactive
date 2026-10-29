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

## The review of a repair is shown the reviews before it (2026-10-29)

Diagnosis, on the Phase 1.5 `test-coverage` failures (`20261029-112618`, `20261028-221352`): the review after a repair
was shown the work alone, not what the review before it had asked for, and failed the step on the very point the worker
had put right as asked - a line number in one run, a branch count (1/6 asked, then 3/6) in the other. With
`ReviewRetries = 1` the second fail rejected the step and skipped the three after it. Fixed on
`feat/retry-review-sees-its-verdict` (`1b2abac`): `StepVerdictInput.Earlier` carries each fail that sent the step back,
and the review of the repair checks their points; the first review of a step is asked as before. Pinned by
`TheRetryReviewSeesItsEarlierVerdictTests`. The criteria review (`SemanticCriteria`, off by default) is unchanged.

Measured at `1b2abac`, same setup as Phase 1.5 with `StepOutputs` + `TypedCriteria` on, `test-coverage` five times:
**right PASS 5/5** (before: 2/3 with the flags on, 1/3 off). Two runs had a repair reviewed - step 2 of `141141`, step 3
of `141529` - and both passed it, the second saying point by point that each earlier point was put right. No false
verdicts. 152-268 s, 303k-960k tokens in; the spread is the worker's (the costliest run read the HTML coverage report).

Stamps: `20261029-140434`, `20261029-140756`, `20261029-141141`, `20261029-141529`, `20261029-142010`.
## Follow-up 5 - an item worked on in another file (2026-10-29)

Read again: in `20261017-231237` the request ("check the coverage, add tests if needed, report in MD") forbade nothing.
The local planner consumed step 3's `gaps` (`Shelf/Inventory.cs`) into "add tests" with `file_write` evidence; the step
wrote the tests into `InventoryTests.cs`, left the library as it should, and the coverage check rejected it twice ("no
file_write evidence") - the only way through was to change the code under test. The same plan shape came up in
`20261018-001423`, which failed earlier on another step. Neither has recurred with Sonnet planning.

Fixed on `fix/forbidden-file-not-write-evidence` (`c71e8c2`): under write evidence, an item whose own file the step left
unchanged while it changed files that are no item of the list is counted, shown to the reviewer in full with the files
changed in its place, and stands only on an approving verdict - without one the step is done, not confirmed
(`CoverageWrittenElsewhere`). A step that changed nothing, or only other items' files, is rejected as before. Pinned by
`AnItemWorkedOnInAnotherFileIsTheReviewersTests`.

A regression check, not a measurement of the fix: `test-coverage` three times at `c71e8c2`, right PASS 3/3; no plan
consumed a list, so the changed path was not reached. Tokens in 790k-1322k, above the morning's 303k-960k - the worker's
spread (the costliest run 56 calls); nothing changed for runs without a consumed list.

Stamps: `20261029-144602`, `20261029-145040`, `20261029-145505`.

## Follow-up 4 - `disk-report` reads a table of volumes (2026-10-29)

The truth `every-drive-named` asked for each fixed volume as `C:`. The runs of 2026-10-17/18 wrote the volumes as a
Get-PSDrive table (`C      248,61  3476,32 ...`) and were scored FALSE PASS for want of the colon. Now a volume also
counts as named where its bare letter heads a row of a table - followed by `|`, or by blanks and a figure - and not
where the letter is inside a word or a path (`C#`, `x/C`, `x\C`). Pinned by `TheDiskReportTruthReadsATableOfVolumesTests`
with this machine's own volumes: the Get-PSDrive table from `20261018-004059` failed before the change and holds now.
`--fixtures`: the truth still fails on the fixture.

Live, four runs: right PASS 2 (`151242`, `151356`), right FAIL 2 (`150951`, `151116`). Both passing reports wrote `C:`,
so the new pattern was not reached live. In both failures the worker named the unlettered volume "system partition /
EFI" with no call behind it. The review failed it, the repair corrected some of the passages and missed others, and
the second review failed it again. That is the review rule on derived claims, not the truth: the rejected report was
put back, and the truth found no file.

Stamps: `20261029-150951`, `20261029-151116`, `20261029-151242`, `20261029-151356`.

## Phase 5 - one confirmed rule against a recurring rejection (2026-10-29)

The plan's measurement for Phase 5: two sets of runs of one scenario, one given a confirmed rule that addresses the
first set's rejection. The bench gives the rule with `--rules` (new): a JSON array of texts kept as confirmed rules in
each run's own workspace - not the machine's Global layer, which the desktop's runs share (`rules.json` there was empty
throughout). `results.json` says how many rules a run was given (`rulesGiven`).

**The rejection.** `disk-report`, all four runs at `91ac265` (stamps under Follow-up 4): the step review failed the
report because it stated what no call showed - the computer's name, the OS, a volume called "system" or "EFI". The
rule, written from those notes as a person would edit the candidate (`bench/rules/report-only-what-was-shown.json`):
"Write in a report only what a command you ran printed. The computer's name, the operating system, a volume's label,
type or role (system, EFI, recovery): state them only when a command's output shows them; otherwise leave them out."

**Runs**, at `feddd5a`, alternated so drift in the models falls on both sides:

| | verdicts | first rejection was for unshown facts | no rejection | tokens | seconds |
| --- | --- | --- | --- | --- | --- |
| without the rule (4) | right PASS 3, right FAIL 1 | 3 of 4 (`183018`, `183253`, `183936`) | 0 | 99k-290k | 60-218 |
| with the rule (4) | right PASS 4 | 2 of 4 (`183408`, `183846`) | 1 (`184041`) | 61k-133k | 44-72 |

Without the rule the fourth run's first rejection was a broken HTML file (`183503`), and it ended a right FAIL after its
repair. With the rule one run's rejection was arithmetic - totals 100 GB off (`183134`). Counting the four earlier runs
too, unshown facts were the first rejection in 7 of 8 runs without the rule and 2 of 4 with it. In favour of the rule,
not settled by it: four runs a side, and the rule did not stop the worker in half its runs.

**Why it did not.** The "unshown" facts are not invented. The engine itself gives the worker `Host: Microsoft Windows
10.0.26300 (X64), 32 CPUs ...; machine EDWARD_PC` - `EnvironmentInfo.Summary()` in `Orchestrator.BuildUserPrompt` - and
the reports copy exactly that line (one wrote "32 ядра", the CPU count from the same line). The step reviewer is shown
the step's calls, not that block, so it judges the engine's own facts unsupported. A rule that says "only what a command
printed" asks the worker to distrust what it was told, and it did so in half the runs. That is an engine defect, not a
lesson: the reviewer should judge a claim against everything the step was given, the run's context included. Open.

Stamps: without `20261029-183018`, `183253`, `183503`, `183936`; with `183134`, `183408`, `183846`, `184041`.

## The step review is shown what the application gave (2026-10-29)

The defect the Phase 5 measurement found: the worker's prompt carries the workspace, the git branch and the host line
(OS, CPUs, machine name), and the step review was shown only the calls - so it failed reports that repeated the
engine's own facts. Fixed on `fix/reviewer-sees-given-facts` (`d94b835`): one text, `Orchestrator.GivenFacts`, goes into
the worker's prompt and into the step review as GIVEN BY THE APPLICATION, and the review is told a claim that repeats it
is shown by it. The criteria review is not changed: it takes evidence only of the kinds each criterion allows. Pinned by
`TheStepReviewIsShownWhatTheApplicationGaveTests` (fails with the review not given them).

Live, `disk-report`, four runs, no rules: right PASS 4/4. All four reports name the machine and the OS, as before, and
no review failed them for it (before: the first rejection in 7 runs of 8). The rejections left are real ones, each put
right on the repair: totals 2000 GB off (`190216`), a volume called "EFI / boot" that no call showed - the given facts
do not cover it, and the review still failed it (`190321`), a folder nobody asked for and a "rate of filling" with no
data (`190111`). One run passed with no rejection (`190520`). Tokens 32k-232k, against 99k-290k before.

Stamps: `20261029-190111`, `190216`, `190321`, `190520`.

## The step review is given the rules (2026-10-29)

The worker and the planner were given the confirmed rules; the step review was not, so a rule was one the worker could
follow or not. On `feat/reviewer-given-rules` (`f2b4b4f`) the review is given them, as WORKSPACE RULES, and told to fail
a step that goes against one unless the request asks for it - the request wins. Pinned by
`TheStepReviewIsGivenTheRulesTests` (fails with the review not given them; a run with no rules is reviewed as before).

Live, `disk-report` with the one rule of the Phase 5 measurement, four runs a side, alternated: the side without is the
console built from `master` at `b9a0cbf` (`--console`), the rule given to the worker only.

| | verdicts | first rejection cites the rule |
| --- | --- | --- |
| rule to the worker only (4) | right PASS 2, right FAIL 1, FALSE FAIL 1 | - |
| rule to the reviewer too (4) | right PASS 4 | 3 (`194159`, `194431`, `194700`) |

With the rule the review held the worker to it: a volume called "system", "EFI" or "recovery" that no call showed, and
the machine's name and OS in the header - each put right on the repair, every run a right PASS. No false verdict on that
side. The FALSE FAIL is on the other side (`194041`): the repair of a wrong "100 %" brought two new unshown claims and
the second review failed it - the review's one retry (`ReviewRetries = 1`), not the rule.

**The rule is stale since the fix above it.** It says to name the computer and the OS only from a command's output; since
the review is shown what the application gave (`d94b835`), those are shown, and the rule now asks for less than the engine
allows. The review rightly put a person's rule above the application's facts and failed the header for them (`194159`,
`194700`). That is what confirmed rules do: they last until a person changes them - the Lessons panel is where. The
measurement file is left as it was measured.

Stamps: worker only `20261029-193817`, `194041`, `194323`, `194533`; reviewer too `193941`, `194159`, `194431`, `194700`.
