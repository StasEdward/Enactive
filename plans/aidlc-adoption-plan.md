# Adopting five AI-DLC mechanisms in the Enactive engine - implementation plan

Date: 2026-10-16

Status: proposal, nothing implemented. Written against `master` at `7dc8866`; the line numbers below are from
that commit and will drift. Revised after two reviews (`aidlc-adoption-review.md`, `aidlc-adoption-resolution.md`);
one question is left open on purpose (open question 6: checks for staged runs).

Source studied: [awslabs/aidlc-workflows](https://github.com/awslabs/aidlc-workflows) (MIT-0), in particular
the [engine and skill system](https://github.com/awslabs/aidlc-workflows/blob/main/docs/reference/17-skill-system.md),
[sensors](https://github.com/awslabs/aidlc-workflows/blob/main/docs/harness-engineering/06-sensors.md),
[rules and the learning loop](https://github.com/awslabs/aidlc-workflows/blob/main/docs/harness-engineering/05-rules-and-the-loop.md)
and [anatomy of a stage](https://github.com/awslabs/aidlc-workflows/blob/main/docs/harness-engineering/01-anatomy-of-a-stage.md).

## Objective

Take the five mechanisms in AI-DLC that answer the engine's standing question - *how does the engine itself
decide that a piece of work is done* - and fit them to Enactive. Do not take AI-DLC's methodology: its 33 stages,
14 personas and 11 scope profiles are authored for a strong model that is also the conductor. Enactive plans are
generated per run, and the worker is often a cheap local model, so every mechanism below is adapted to that.

| # | AI-DLC mechanism | One-line adaptation |
| --- | --- | --- |
| 1 | `consumes` / `produces` + `upstream-coverage` sensor | Derive coverage of a consumed list from the plan's structure, check it at the consuming step's gate |
| 2 | Sensors (deterministic checks, `fire_on`, `matches`, severity) | Declarative workspace checks bound by the files a step changed, able to refuse a step before the reviewer |
| 3 | Engine `next` / `report` / `park` with a typed directive union | Ship "pause after this step" independently; defer step-commit ownership and directive refactors |
| 4 | Stop hook + no-progress counter on a position signature | Replace the once-only nudges in `StepEnding` with a counter that keeps nudging while the worker makes progress |
| 5 | Learning loop with a human gate and layered rules | Lesson candidates at the end of a run; only confirmed ones become workspace rules for the next run |

Each phase follows `AGENTS.md`: general rules only (nothing that one scenario needs), every change with a test that
fails without it, and live-model behaviour measured with `bench/`.

## What the code looks like today

The comparison was made by reading the engine, not from memory. The short version: Enactive already has most of
the *machinery* AI-DLC has, sometimes better (baselines, flaky re-runs, per-step bisection). The gaps are in
**where** the machinery fires, **who has to remember to use it**, and **who commits the state**.

| AI-DLC has | In Enactive | Gap |
| --- | --- | --- |
| A stage declares `produces` | `PlanStepSpec.Output` (`Core/Tasks.cs:107`) - a typed `StepOutputSchema` with `path[]`, `string[]`, `results` fields | Behind `EngineOptions.StepOutputs`, **off by default** |
| A stage declares `consumes` | Nothing. `StepOutputContract.ForDependents` (`StepOutputContract.cs:437`) hands every upstream field to every dependent | No contract of what a step uses |
| `upstream-coverage` at the gate | `EvidenceCoverage.Evaluate` (`EvidenceCoverage.cs:167`) joins a source list to a results map plus per-item evidence ("covered 9 of 12") - deterministic | Exists only if the **planner writes** a `covers_all` criterion; evaluated only at **run end**; behind `TypedCriteria` (off) |
| Fan-out per item | `ForEach` + `FanOut.Validate/Steps/Join`, `DagScheduler.Expand` | Behind `DynamicSteps` (off) |
| Exit code is authoritative, the worker's claim is not | `SuccessEvaluator` ignores the tool's own success flag (`SuccessEvaluator.cs:217-225`); `RanNoTests` -> Unknown | Already right |
| Sensor bound by the stage, fires on write or at the gate | `SuccessCriterionDefinition.Step` (only typed file checks run at step end, `StepEnding.cs:195`); build/test only at wave end and run end | No per-write checks; no user-declared step checks; no glob |
| `blocking` refuses the gate | `CriterionResult.Blocking` decides the **run** outcome in `RunOutcomeDecision.Settle` | Nothing deterministic can refuse a **step** before the LLM reviewer; build regression is hard-coded advisory (`Origin = System`) |
| Anti-tamper of tests | `.enactive/` reserved; delete approvals; `BuildRegression.CompareTests` counts pass->fail only | A deleted or renamed test is invisible |
| Engine `next` emits one typed directive | `DagScheduler.NextReadyBatch` (`DagScheduler.cs:177`) **claims** a batch; `StepDispatcher.RunAsync` pumps | The directive set exists only implicitly |
| `report` is the only state mutator | Commit is spread over the `RunStepAsync` closure (`Orchestrator.cs` ~1620-1760): outcome map, revert, wave ledger, publish, digest, events, `MarkDone`, checkpoint - in a load-bearing order | Checkpoint is a snapshot of in-memory state, not its source |
| `park` at an inter-stage boundary | Question-park mid-step (`DecisionPendingException` -> `ParkHere`, `NeedsUser`); `Blocked` (Phase 7) | No user-initiated "pause after this step"; the limit path calls `AbandonPending` and skips steps |
| Stop hook, counter keyed on position, released after N | `StepEnding.EndAsync` (`StepEnding.cs:113`): one nudge per obligation via booleans `_repairRequested`, `_criteriaNudged`, `OutputSlot.Nudged`, `_openCallsNudged`; `StepProgress` stall counter keyed on calls (`StallLimit = 3`) | Second nudge never happens even when the first one produced progress |
| Diary + candidates at the gate + layered rules | `MemoryEntry` (Decision, Outcome) written **automatically** by `RunRecorder` (100-133) when `ProjectFacts.WorthRemembering`; injection into prompts **commented out since 2026-09-28** (`Orchestrator.cs:4604`, run 9c1a061b); instructions are Global + per-role only (`DefaultWorkers.Augment`) | No confirmation, no workspace rule layer, nothing learned reaches the next run |

### Why the 2-of-12 wiki audit can still pass green

Even with all three flags on, the run passes when the planner simply does not write `covers_all`: step 1 declares
`pages: path[]`, step 2 depends on it with a `text` output, `PlanValidation.Error` checks only ids and cycles, and
nothing derives the obligation from the structure. If the planner does write it, the criterion is evaluated after
step 2 was already reviewed and marked done. Phase 1 closes exactly this, without any scenario-specific rule.

## Order of work

```
PR 0     a still-failing step criterion no longer ends the step Succeeded  (small, closes a silent hole)
PR 0b    failure path: StepCompleted published before MarkFailed releases a join  (race fix)
Phase 1  upstream coverage       (small model change, high value, closes the known hole)
Phase 3.4 disappeared tests + Protects  (independent of the check manifest; early)
Phase 2  obligation loop         (local to StepEnding; needs structured refusal state in OutputSlot first)
Phase 4.2 pause after a step     (independent; uses StepDispatcher's hold; step-commit refactor deferred)
Phase 3  workspace checks        (new manifest + step gate; uses Phase 1's gate seam)
Phase 5  learning loop           (UI-heavy; uses Phase 3 to turn a lesson into a check)
```

PR 0, PR 0b, Phase 1 and Phase 3.4 can be done in parallel. Each phase is a branch and a PR of its own; a phase
lists its PR-sized parts. Nothing is merged until the phase's bench measurement is in the PR description.

### PR 0 - a still-failing step criterion is not `Succeeded`

`StepEnding.EndAsync` nudges once when a step's own criterion fails (`StepEnding.cs:195`). If the criterion still
fails on the next turn it is not checked again, and the step ends `Succeeded` with no event;
`StepEndingTests.cs:129-141` pins exactly that. The output nudge (`:215`) emits no event either.

- A criterion that still fails after its nudge does not end the step `Succeeded`: the step goes to review with the
  failure as engine evidence, or ends `Incomplete` when it is not reviewed.
- Every nudge and every release is an event, so a run's record shows what was owed and what was let go.
- Rewrite the pinning test to the new behaviour (red before, green after).
- Correct the stale comments at `RunRecorder.cs:97-99` and `ProjectFacts.cs:6-9`, which still say memory is read
  into the prompt (it has not been since 2026-09-28), and the doc comment at `EvidenceCoverage.cs:164-165`
  ("as they ran" - the numbers are plan positions).

### PR 0b - the failure path releases a join before its step is reported

On Failed/ReviewRejected/Incomplete the commit tail calls `scheduler.MarkFailed` **before** publishing
`StepCompleted` (`Orchestrator.cs:1737-1762`), and a join treats a Failed or Skipped item as ended
(`DagScheduler.cs:312-313`). A join can start before the failing item's completion is published - the race the
comment at `Orchestrator.cs:1663-1678` describes for the success path. Publish first, mark second, as the success
path does, with a test that asserts the order. Phase 4.0 must not pin the current order as correct.

---

## Phase 1 - consumes, derived coverage, coverage at the step gate

### 1.1 `Consumes` on the plan step

- `Core/Tasks.cs`: add consume declarations (source step, field, evidence kind and subset policy) and explicit
  ordering-only `After` references to `PlanStepSpec` and `PlanStep`; copy them in `DagPlan.FromSpecs`.
  Share the source-reference shape with `ForEachSource`; a `forEach` is by definition a consume.
- `Planner.cs`: parse `"consumes": [{"step": 0, "field": "pages"}]` next to `ParseForEach` (L359); write it back when
  re-planning (~L180); one paragraph in `StepOutputsPrompt` (~L589). The prompt says what a consume costs: *a list you
  consume is a list you must cover*. Parse and round-trip `"after": [0]` as ordering-only dependencies.
- Not in `PlanValidation.Error`: that is a pure `string?` check in Core that cannot change a plan, and it runs
  before outputs are stripped when `StepOutputs` is off (`Orchestrator.cs:512` vs `:621`) and again on resume
  (`:637`), so it would trigger replans for nothing. Use a plan transform next to `FanOut.Validate` (L629): a
  consume must name an earlier step that the consumer depends on (directly or not) and a field that step declares.
  A missing dependency is added - `FanOut.Validate` does this for `forEach` today but **silently**; here the
  addition is recorded in the plan notes.
- Persist `Consumes`/`After` and the derived criteria in `CheckpointStep` (`Checkpoints.cs:21-55`), the checkpoint
  write and read (`Orchestrator.cs:1345-1347`, `PlanOf` L2351-2361) and the `ReplanAsync` serializer
  (`Planner.cs:170-185`). `ForEachSource` is a `sealed record` serialized in checkpoints (`Checkpoints.cs:38`):
  share its JSON shape, not its type.

- With the coverage contract enabled, a dependency on a step that produces lists creates a coverage obligation by
  default. The planner must explicitly declare the consumed list fields, or classify the edge as ordering-only
  with `after`. An unclassified dependency is rejected and returned for replanning, never silently treated as
  ordering-only. All list fields on a data edge must be accounted for; reject conflicting `after` and `consumes`
  declarations for the same source. An ordering-only edge hands on no structured data.
- Preserve these declarations and the derived criteria through checkpoint serialization and resume. Legacy runs
  without the new contract retain their recorded semantics; they must not silently claim the new coverage guarantee.
  The contract version lives on the plan in the checkpoint (`PlanContractVersion`), not on templates: a
  `TaskTemplate` holds a goal, parameters, criteria and limits but no steps (`Core/Templates.cs:260`), and every run
  plans afresh, so there is nothing in a template to migrate.

The default obligation and the required declaration are distinct: omission is an error, not an escape from coverage.
`after` expresses ordering ("after the build"); its consistency with the request remains part of plan review.

### 1.2 Coverage derived from the structure, not written by the planner

- New `Agents/UpstreamCoverage.cs`, deterministic: for every `(consumer, ref)` where `ref` is a `path[]`/`string[]`
  field, produce a `TypedCriterion(EvidenceCoversAll)` with `SourceStep/SourceField = ref`,
  `ResultsStep = consumer`. The results field is the consumer's `results` field, or its own items when it is a
  `forEach` over the same list (the `perItem` case `EvidenceCoverage.Invalid` already accepts).
- Evidence kind is declared separately from the list type: `mentioned`, `file_read`, `file_write`, or `command`.
  Default to `mentioned` and label it honestly in prompts, events and reports: the item appeared in the arguments
  of a successful call; this proves neither reading nor completion of the requested action. Do not infer reading
  from `path[]`. Preserve the legacy `call` interpretation when loading old criteria.
  Reads require complete read-ledger evidence; writes require recorded successful writes to the item's path;
  command evidence requires a recorded successful command associated with that item. Neither mere mention nor
  a successful unrelated command proves semantic correctness; the reviewer still judges the requested work.
- A filtering consumer may declare `subset: true`, with separate `processed` results and `excluded` reasons.
  The engine checks that their keys are disjoint, contain no foreign items, and their union equals the source list;
  every exclusion needs a reason, and every processed item needs its declared evidence. The reviewer judges whether
  the request permits the exclusions. With actual exclusions and no usable approving review, the step cannot be
  `Succeeded` (`DoneUnverified` if otherwise finished); a reason alone never authorizes an omission.
- `mentioned` is cheap to satisfy: `EvidenceCoverage.Gather` counts an item when any string in a successful call's
  arguments equals it (`EvidenceCoverage.cs:83-85`), so one call naming all twelve paths covers all twelve.
  Coverage resting only on `mentioned` therefore cannot yield `Succeeded` without an approving review
  (`DoneUnverified` at most), and the per-item evidence kind is part of what the step reviewer is shown, not only
  of the report. The report distinguishes "every item accounted for" from "every file read".
- Called after `FanOut.Validate` (`Orchestrator.cs` ~629) and before `TypedCriteria.Validate` (~666). A consume that
  `EvidenceCoverage.Invalid` refuses (no results field on a consumer of a list) **refuses the plan** with the reason
  and goes back to the planner once - not dropped with an `ErrorObserved` as today (`Orchestrator.cs:668-669`).
  This point is after the only structural replan (L512-537), so it needs its own `ReplanAsync` call with the
  diagnosis, on the pattern of L541-605.
- If the replan does not fix it, do **not** fall back to the original plan (`before`) - that is the same hole by
  another door. The plan proceeds with the derived criterion kept; it cannot pass, so the run ends `Incomplete` with
  the reason, never green.
- Planner-written `covers_all` keeps working; a derived criterion that duplicates one is de-duplicated by
  the complete obligation, including evidence kind and exclusion policy; never discard a stronger explicit check
  because a weaker derived check names the same source and result.

### 1.3 Coverage at the consuming step's gate

- Seam: after the step's output is accepted and before the reviewer, including before the `!models.ReviewOn`
  early return in `Orchestrator.Attempt.cs`. Run `EvidenceCoverage.Evaluate` for this step using committed upstream
  outputs plus the candidate built from its `OutputSlot`; the candidate is not yet in `session.Outputs`.
- Failed coverage does **not** call the reviewer: it becomes a step verdict `Fail` with `RepairAdvice` = the
  uncovered items. Give this gate its own bounded retry budget, independent of `ReviewOn` and `ReviewRetries`,
  while respecting the run's total limits. Reuse the repair mechanism with feedback labelled as an engine check.
  Files stay in place between attempts, as they do today; existing rollback applies after final rejection. After the budget the step ends
  `ReviewRejected` with the reason "covered 2 of 12; not covered: ...".
- Today only the reviewer produces `Fail` (`CriteriaReview.cs:134`, `StepVerdictReview.cs:233`). The gate builds one
  itself on the `OutOfBudget` precedent (`Orchestrator.Review.cs:65-66`) and publishes its own `ReviewFailed`
  with an engine origin, since the reviewer's emission (`Orchestrator.Review.cs:170-171`) is bypassed.
- Before the full retry, use the cheaper seams that exist: `CallAdmission.cs:733-736` already tells a step at
  submit time which items have nothing behind them, and `StepEnding.cs:190-208` checks a step's criteria as it ends.
  A retry is the last resort, not the first signal.
- For fan-out, evaluate coverage at the join but never reject the join wholesale for incomplete coverage.
  Preserve partial outputs and the account of unfinished items for downstream reporting. Uncovered items keep
  the join unverified and produce a blocking run-level coverage result, so a later report cannot turn the run green.
- The run-end evaluation stays; it now confirms what the gate already decided.
- Numbering is currently stable: `DagScheduler.Expand` appends item steps, preserving original plan positions,
  and `stepNumbers` uses those positions. Keep a characterization test across expansion and resume; a move to
  `Guid` would be a separate improvement, not a prerequisite justified by an unconfirmed numbering bug.

### 1.4 Hand-off filtered to what is consumed

- `StepOutputContract.ForDependents` (L437, call site `Orchestrator.cs:1460`): when a step declares `Consumes`, hand
  on only those fields (plus a one-line digest of the rest). Smaller prompts for a local model; and a step cannot
  receive undeclared structured fields. `after` hands on no structured output. An unclassified list dependency
  is rejected in the new mode; only legacy mode retains today's implicit hand-off. Prompt filtering alone is
  not an access boundary: files and other context may still expose the same information.

### 1.5 Defaults

- `StepOutputs`, `TypedCriteria`, `DynamicSteps` stay opt-in until the bench shows no regression with them on; then
  one PR turns on `StepOutputs` + `TypedCriteria` together (derived coverage needs both). `DynamicSteps` is judged
  separately - fan-out multiplies model calls.
- Add the three to the settings UI if they are not there, with a hint on what each costs.

### Tests (new behavior verified red with its fix reverted; characterization preserves existing behavior)

- Plan with `pages: path[]` -> dependent with no `consumes`, `after` or `covers_all`: refused and replanned.
- Explicit consume with only a `text` output: refused; explicit `after` is accepted and receives no list output.
- Consumer covers 2 of 12 with a `FakeChatProvider`: step ends `ReviewRejected`, reviewer **not** called
  (assert the fake reviewer received zero calls - a test green in both directions is not a test).
- Repeat the incomplete-coverage test with review disabled: the gate still runs and uses its own retry budget.
- With explicit `file_read`, covers 12 of 12 but 3 without complete read evidence: rejected with those 3 named.
- Default `mentioned` is labelled as such; mentioning a path cannot satisfy `file_read` or `file_write`.
- Successful writes and command evidence satisfy only their declared contracts, from recorded tool results.
- `subset: true`: processed/excluded partition the source; missing reasons, overlaps, gaps and foreign items fail.
  Valid exclusions pass only with reviewer approval; without review they cannot yield `Succeeded`.
- Incomplete fan-out preserves its partial join output and downstream report, but cannot produce a green run.
- Characterization: expansion preserves source/result numbering. New contract tests cover checkpoint round-trip.
- `ForDependents` hands on only consumed fields.

### Measurement

`wiki-drift` and `test-coverage`, flags on, five runs each before and after. Expect: no run green with uncovered
items; the cost (model calls, tokens) reported next to it.

---

## Phase 2 - an obligation loop that releases on no progress

### Problem

`StepEnding.EndAsync` nudges once per kind of obligation (`StepEnding.cs:93-102`). When a nudge works partially
(the worker wrote 4 of 6 output fields, or fixed one of two failing calls), the next turn ends the step anyway.
AI-DLC's Stop hook keeps the work going while the *position* changes and lets go only after N blocks without
change.

### Prerequisite: refusal state the engine can see

The "4 of 6 fields" state does not exist today. A refused submission is not stored ("Nothing was stored",
`CallAdmission.cs:747`); `StepOutputSlot` (`StepOutputContract.cs:14`) keeps only the last **accepted** values, and
`LastRefused` is a message string. Add a structured diagnosis of the last refused submission to the slot
(`MissingFields`, `InvalidFields`), kept apart from the accepted result. The signature is built from it.

### Change

- New `Agents/ObligationLoop.cs`:
  - `Signature` = step number + sorted ids of failing step criteria + the slot's structured refusal state (which
    required fields are missing or invalid) + `OpenFailures.Describe()` keys + a pending text-described call.
    Deterministic, built only from the engine's own state - never from the model's text.
  - `Nudge(signature)` returns `Nudge` while the obligation's kind is under its limit, the step is under its total
    ceiling and the signature has not been **seen before** in this step; otherwise `Release`. A new signature
    counts as progress; a signature seen before does not, so A -> B -> A alternation cannot reset the count forever.
    An empty signature means nothing is owed.
- `StepEnding`: the four flags become calls into one `ObligationLoop`. Their lifetimes differ today: the
  `StepEnding` flags reset on every `RunToolLoopAsync` (every retry or repair), while `OutputSlot.Nudged` survives
  retries (`RunSession.cs:99-100`). The loop's state lives in one declared place - per attempt - and the test says so.
  The rule order of `EndAsync` stays as it is. `Release` keeps today's outcomes (`Incomplete`, handed to review,
  `Blocked`), apart from PR 0's fix.
- Limits: `EngineOptions.ObligationNudges` = a limit **per kind of obligation** plus a total ceiling per step, with
  an attended and an unattended pair. Unattended is a run property read from `RunScope`, not `CanApprove`: that
  one means "an approval is possible at all" and is also false for console `--approve deny`
  (`App.Console/Program.cs:721`); `StepEnding` has no access to decisions anyway.
  **The compatible default is 1 per kind, total ceiling = number of kinds** - that, not "1/1", is today's behaviour,
  because today each kind has its own once-flag. The bench decides new values, under a hard ceiling in code with
  the reason in a comment (AI-DLC caps at 2 interactive / 8 autonomous for a different unit of work - do not copy).
- Releases without a nudge, as AI-DLC's Stop hook has human-wait carve-outs: the step waits for a person's answer,
  the step is `Blocked`, or the turn budget is exhausted.
- Context guard: every nudge adds a turn; with a local model at 8k `num_ctx` that is real. Not `EvidenceBudget` -
  that is the characters of evidence shown to the reviewer (`EngineOptions.cs:145`). Use the window the engine
  already knows at step level (`Turn.ContextWindow`/`PromptTokens`, `Orchestrator.cs:3366-3367`, `:4134-4136`) and
  the existing trim/handover/"window full" handling (`Orchestrator.cs:3837-3935`). When the provider states no
  window, the loop does not guess one; the ceiling applies.
- The existing stall counter stays (`StepProgress.Stalled`, `StallLimit = 3` at `Orchestrator.cs:50`): it watches
  *calls*, this watches *obligations*. A step can trip either.

### Tests

- Refused submission with 4 of 6 required fields: the slot records which 2 are missing; the accepted values are
  unchanged.
- 4/6 after nudge 1, 6/6 after nudge 2 -> `Succeeded` with a per-kind limit of 2 (fails today: ends after nudge 1).
- Same signature twice -> released after the limit; A -> B -> A -> released at the ceiling, not looped.
- Signature built from state, not text: a model that repeats "I fixed it" without changing state is released.
- Default limits reproduce today's nudge sequence for every kind (characterization).
- Unattended is taken from the run, not from `CanApprove`: a `--approve deny` attended run uses the attended pair.

### Measurement

`missing-input`, `setup-step`, `disk-report` with limits 1, 2, 3: completed-vs-incomplete counts and tokens.

---

## Phase 3 - declarative workspace checks

### Adaptation

AI-DLC binds a sensor to an authored stage by id. Enactive's steps are generated per run, so binding by step id is
not possible. A check is bound by **what a step changed** (`matches` globs over the step's changed paths) and,
optionally, by a template or the plan (a step may name check ids from a one-line catalog, the same pattern as the
tool catalog in `Docs/TOOL_CATALOG_PLAN.md`).

### 3.1 Manifest and store

- `Core/Checks.cs`: `CheckDefinition(Id, Description, Command, IReadOnlyList<string> Matches, CheckFire FireOn,
  CheckSeverity Severity, int TimeoutSeconds, IReadOnlyList<string>? Protects)` with `CheckFire { Write, Step, Run }`
  and `CheckSeverity { Advisory, Blocking }`.
- Stored in `<workspace>/.enactive/checks/*.json` (store modelled on `Workspace/TemplateStore.cs`) and globally next
  to `settings.json`; workspace wins by id. `.enactive/` is reserved for tools (`WorkspaceGuard.cs:26-55`, enforced
  in `ResolveInside` via `TouchesReserved`), so the worker's **file tools** cannot edit the checks that judge it.
  No reserved-folder check was found for **shell** commands (`RunCommandTool`, `RunPowerShellTool`); until a test
  shows a shell command cannot write `.enactive/checks/`, the anti-tamper claim is not made. Closing that gap, or
  hashing the manifests at compose and refusing a changed one, is part of 3.1.
- Loaded once at compose time; save the full resolved check definitions in the run record and checkpoint, not
  just ids or hashes. Resume uses this snapshot even if manifests were edited or deleted. Keep pending/deferred
  check obligations with it, so a pause cannot lose a required check. Current permissions still govern execution.
- Validation on load: unique id, non-empty `matches` for `Write`, command non-empty; an invalid file is reported in
  the UI and skipped, never crashes the run.
- Built-in ecosystem build/test (`Core/Ecosystems.cs`) are exposed as implicit checks with ids `build` and `test` so
  a workspace can raise them to `Blocking` per step without redeclaring the command. The only implementation today
  is `DotnetEcosystem` (`Workspace/KnownEcosystems.cs`), so the implicit checks exist for .NET workspaces only;
  others declare their commands. Say so in the UI rather than show empty ids.
- Raising build regression to blocking reverses an explicit decision (`RunOutcomeDecision.cs:122-126`, run 3fe4f8:
  the workspace can change around a run). It stays opt-in per check, and the PR cites that decision.

### 3.2 Execution

- Every check maps to a `SuccessCriterionDefinition` and runs through `SuccessEvaluator` - same permission gate,
  same "exit code decides", same `RanNoTests`. No second runner.
- Permissions: engine checks run only when policy allows the command without asking
  (`BuildRegression.WhyNotAllowed`), so in unattended or ask-mode runs a blocking check is often `Unknown`, hence
  `DoneUnverified`. That is shown in the UI as "not run: needs permission", never skipped silently.
- `FireOn = Write`: after a successful write - the hook is `ToolResultAccounting.Record`
  (`ToolResultAccounting.cs:16-20,57`), not `CallAdmission.cs:792`, which describes a delete *before* it is
  approved. Glob-match the changed paths and run the matching checks; the result is **appended to the tool result**
  the worker sees (fast feedback), and recorded as an engine journal entry. Always advisory, as in AI-DLC - a
  write-time refusal would fight the worker mid-edit. Debounced per step (one run per check per turn) and only for
  checks with `TimeoutSeconds` under a small limit. In staged mode write checks are **skipped**, not deferred: they
  exist to inform the worker mid-step, and at apply there is no worker left to inform.
- `FireOn = Step`: in the first version, blocking step checks execute here only with sequential steps and no
  staging. Use measured changes plus tool-recorded changed paths; `StepFilesNowAsync` is review presentation,
  not an isolated authoritative changeset. Define handling of command effects with unknown paths explicitly;
  unknown attribution must not silently mean no matching checks.
  `Blocking` + failed -> verdict `Fail` with the check's output tail as
  repair advice, reviewer not called; `Advisory` -> journal evidence for the reviewer
  (`ToolCallOrigin.Engine`, the `engine_checked_step_criteria` pattern at `Orchestrator.Review.cs:117`).
  `Unknown` (did not run) on a blocking check -> `DoneUnverified`, never `Succeeded`.
- With parallel steps, defer blocking checks to the stable wave boundary after in-flight steps drain. A deferred
  failure is attributed to that boundary rather than falsely blamed on one parallel step.
- With staging, blocking checks cannot run during the run: `TypedCriteria.Evaluate` and every command read the disk,
  where staged writes are not. Record the deferral visibly and persist it; pending checks are not passes and cannot
  support a green result - a staged run ends at best "not verified: checks pending apply". Checks against the old
  on-disk files never discharge the obligation.
- **Open - where a staged run's checks run.** Apply is not one moment: `ServerStaging.ActAsync` applies or rejects
  **each file separately**, by a person, after the run has ended (`ServerStaging.cs:134-178`). Unresolved:
  - when to fire: after each file (a build on "3 of 7 applied"), after the last decision (a state the person
    composed, possibly with rejections, not what the run produced), or only on an explicit "apply all";
  - whose status it is: the run's outcome is already recorded, so "applied, checks pending/failed" belongs to the
    proposal set - a new state, new events and UI on two hosts;
  - candidate v1: no automatic checks at apply; a "Run checks now" action on the proposals page runs them on the
    current state, records the result against the proposal set and rolls nothing back. Automatic checks wait for an
    atomic "apply all". An isolated copy with the staged changes laid over the workspace is the later stage that
    checks before the workspace changes.
  The owner decides before 3.2 is implemented for staged runs.
- `FireOn = Run`: joins the final checks (`Orchestrator.cs:2301`) with a non-System origin, so `Settle` can block.

### 3.3 Records

- No new event kinds: `CriterionEvaluated` already carries one check. Its payload today is `criterion`, `outcome`,
  `required`, `exitCode`, `origin` (`Events.cs:760-771`) - **no detail**; the output appears only in the summary
  text. It gains `checkId`, `firedOn` (`write|step|wave|apply|run`), the requested boundary, any deferral reason and
  `detailPath`. The UI folds it as today.
- Full untrimmed output goes to `.enactive/checks/runs/<runId>/<step>-<checkId>.txt`. The summary keeps the
  1200-character trim it has now, which is head **and** tail (`SuccessEvaluator.cs:283-292`), not a tail.
- Removing that folder with the run is new work: `Core/RunHousekeeping.cs` is pure filtering with no IO, and deleting
  a run removes only `*_{runId}.json` (`Workspace/JsonRunStore.cs:39-48`).

### 3.4 Protected files

- `Protects` globs (e.g. `tests/**`): their hashes are taken with the baseline (`Orchestrator.cs:848`, saved by
  `BaselineStore`). At the step gate, a protected file the step **removed or renamed** is a blocking finding; an
  **edited** one is shown to the reviewer as a diff with the note that it is protected. A request that asks for test
  changes is the legitimate case - the planner marks such a step, and the finding becomes advisory for that step only.
  Blocking findings obey the same sequential/non-staged restriction and wave/apply deferral as other checks.
- `BuildRegression.CompareTests`: count tests that **disappeared** from the baseline, not only pass->fail.

### 3.5 UI

- Settings: a Checks section (global) and the workspace card (workspace): list, add, edit, enable, severity.
- Step card: checks that fired, outcome, link to the detail file. Server web UI: the same through `Enactive.Application`.

### Tests

- Blocking step check fails -> step `ReviewRejected` after retries, reviewer received zero calls.
- Advisory check fails -> reviewer receives it as engine evidence; outcome decided by the reviewer.
- Write-time check output appears in the tool result the fake worker receives.
- A check edited or deleted after compose does not change the running run or a checkpoint-resumed continuation.
- A parallel step check runs only after the wave drains. A staged run never runs a blocking check against old disk
  content and ends "not verified: checks pending apply". A pause/resume before either boundary retains the
  obligation, and pending checks cannot turn green.
- A shell command cannot write `.enactive/checks/` (or a changed manifest is refused) - before the anti-tamper claim.
- A blocking check the policy would ask about is `Unknown` and shown as such, never `Succeeded`.
- Deleted test under `Protects` -> blocking finding; `CompareTests` reports the disappearance.
- `Unknown` on a blocking check never yields `Succeeded`.

### Measurement

`build-error` and `test-coverage` with `build`/`test` raised to blocking at step level.

---

## Phase 4 - a clean pause; step commit and directives deferred

Ship pause/resume independently of the state refactor. Sections 4.0, 4.1 and 4.3 describe deferred work;
they are not prerequisites for 4.2 and have no implementation commitment until the ownership design is settled.

### 4.0 Pin today's ordering

Characterisation tests before any refactor: for each step outcome, the exact order of events, `MarkDone`/
`MarkFailed`, checkpoint write and digest line (the race comments at `Orchestrator.cs:1663` say why the order
matters). These tests must pass unchanged after 4.1. They pin the order **after** PR 0b, not today's failure-path
order. The commit is not only in `RunStepAsync`: `JoinAsync` (L1925-1991) and `ExpandAsync` (L1771+, checkpoint at
L1832) commit separately, and checkpoints are also written at L2030, L2166 and L2197 - the suite covers all of them.
`RunSession` (`RunSession.cs:15-37`) already gathers half of the proposed `RunState`.

### 4.1 `StepCommit` - deferred pending state ownership and persistence failure design

- Candidate design, not yet approved for implementation: `Agents/StepCommit.cs` with
  `Apply(RunState state, StepReport report) -> CommitResult(RunState Next,
  IReadOnlyList<WorkEvent> Events, IReadOnlyList<SideEffect> Effects)`. Pure. `StepReport` = step id, outcome,
  record, output, reason. `SideEffect` = revert files, publish output, write checkpoint.
- `RunState` gathers what is spread today: `stepOutcomes`, `RunSession` (Outputs, Records, ReasonOf, Digest, Waves)
  and the scheduler's statuses.
- Before implementation, decide who exclusively owns state transitions, where concurrent reports are serialized,
  and when a transition becomes committed. Serializing effects after two `Apply` calls read the same old state
  does not prevent a lost update. Define recovery after revert, publication or checkpoint-write failures, including
  effect replay/idempotency. `RunCheckpointWriter` currently catches save errors; it is not a transaction boundary.
  A consolidated snapshot alone does not make the checkpoint the source of truth.
- Closed outcome handling must use switch expressions and explicitly promote `CS8509`/`CS8524` to errors for the
  relevant build, despite `TreatWarningsAsErrors=false`, plus tests over every named enum value. Handle unnamed
  numeric enum values explicitly as invalid; a catch-all must not hide new named outcomes. Verify the compiler
  guard with a deliberately omitted named case. A switch statement without `default` supplies no such guarantee.

### 4.2 Pause after the current step (AI-DLC `park`)

Named **Paused**, not Parked: `ParkedRuns`, `ParkedPosition` and `ParkedDecision` already mean the question-park,
and a second "parked" would be ambiguous in code, events and UI.

- A host action "Pause after this step" (desktop run header, server run page, console signal). It goes through the
  `hold` callback `StepDispatcher.RunAsync` already takes (`StepDispatcher.cs:30`); no new step is dispatched,
  in-flight steps **finish** (unlike Stop, which cancels).
- When `inFlight == 0`: commit, checkpoint with `RunCheckpoint.Paused = true`, end the run with a new
  `RunOutcomeKind.Paused`. Outcomes are stored **by name** (`Events.cs:813-818`, `Checkpoints.cs:11-13`), so the
  enum position does not matter; the real cost is the consumers that must place the new value - about nine:
  `RunHousekeeping.DidNotFinish` (a test walks it), `InboxLines`, `RunReport`, `RunFeed`, `RunOutcomeWords`,
  `RunStanding`, `RunOutcomeDecision`, `Remote.Host/EventMapping`, `App.Console/Program.cs`. No
  `AbandonPending`, no skip cascade; `StopsAtQuestions` keeps the ledger and baselines as it does for `Blocked`.
- Resume reuses `ParkedRuns.CheckpointForAsync`; nothing is redone, because a pause only happens at a boundary.
  Today a **crash** resume redoes the running step (`DagScheduler.cs:60`); a question-park resume already carries on
  from where the step stopped (`ParkedRuns.cs:13-15`). A pause needs neither.
- Inbox item "paused at step N of M"; schedules treat a paused run as not finished.
- This is not `NeedsUser`: that one parks mid-step on a question and stays as it is.
- Use the existing completion path; 4.1 is not required. Retain full check/rule snapshots and any pending wave/apply
  checks in the checkpoint. Do not report a successful resumable pause unless checkpoint persistence succeeded;
  expose a save failure explicitly instead of promising a resumable boundary that was not stored.

### 4.3 `Next` without claiming (AI-DLC `next`)

- `DagScheduler.PeekReady(state)` - pure, returns the ready steps without marking them; `Claim(ids)` marks them.
  `NextReadyBatch` becomes `Claim(PeekReady(...))`.
- `RunDirective` union, internal only: `RunStep | Expand | Join | ValidateWave | Verify | Settle | Paused |
  NeedsUser | Blocked | Done`. `RunEngine.Next(RunState) -> IReadOnlyList<RunDirective>` and `StepDispatcher`
  becomes the loop that acts on directives. A directive is checked before it is acted on (e.g. never `RunStep` for a
  step that is not `Pending` with its dependencies met); a violation is a thrown invariant, not a run outcome.
- Deferred with 4.1. Reconsider after ownership and recovery are designed; pause/resume ships without this union.

### Tests

- Deferred 4.1: 4.0 characterization suite unchanged; named enum exhaustiveness enforced by compiler diagnostics
  and enum-value tests, with invalid numeric values rejected explicitly.
- Deferred 4.1: concurrent completions cannot lose updates; inject effect/save failures and restart at each boundary.
- Pause with two steps in flight: both finish, nothing is skipped, resume runs exactly the remaining steps.
- Pause with a failed checkpoint write does not claim a safely saved resumable state.
- Every consumer of `RunOutcomeKind` places `Paused` (the `DidNotFinish` walk and an enum-value test).
- Deferred 4.3: `PeekReady` called twice returns the same set and changes nothing.

---

## Phase 5 - lessons confirmed by a person, applied from the next run

### Adaptation

AI-DLC has the conductor (a strong model) keep a diary during every stage. A local worker writing a diary costs
context and tends to invent. In Enactive the candidates come from two cheaper and more honest sources, at the end of
the run rather than per step:

- **Engine-observed deviations**, deterministic: review rejections and their reasons, repaired or diagnosed checks
  (`CheckDiagnosis`), refused calls (`WriteBoundary`, `ChangeLimitGuard`), decisions the person made
  (`DecisionResolved`), coverage misses from Phase 1.
- **The reviewer's note - deferred to a separate PR.** The step reviewer (the strong model) could add an optional
  `lessons` field to its verdict with the four AI-DLC headings - Interpretations, Deviations, Tradeoffs, Open
  questions - at no extra call. The parse is cheap (the verdict is plain JSON read field by field,
  `StepVerdictReview.cs:177-235`), but the prompt is tuned against measured runs (comments at L76-124) and the answer
  is capped at 2048 output tokens: new instructions there can shift verdicts. The first version uses engine-observed
  deviations only; the `lessons` field comes later with its own bench measurement of verdict stability.

### 5.1 Rule store

- `Core/Rules.cs`: `RuleEntry(Id, Scope, Heading, Text, SourceRunId, At, Status)`; `RuleScope { Workspace,
  Global }`; `RuleStatus { Candidate, Confirmed, Dismissed }`. `IRuleStore` with list, add, set status, update, delete,
  in Json/Sqlite/MySql behind a factory on `ENACTIVE_STORE`, like `MemoryStoreFactory`/`InboxStoreFactory`.
- Scoping is not uniform today: MySQL filters by workspace id (`MySqlMemoryStore.cs:79`), while SQLite and JSON are
  scoped by **file location** (`<ws>/.enactive/enactive.db`) with no workspace filter (`SqliteMemoryStore.cs:77-79`).
  Workspace rules fit that; **Global rules have no home there** and need a machine-level store next to
  `settings.json` (or a global table on MySQL).
- `RunRecorder` (100-133) writes **candidates**, not facts. The automatic `Decision`/`Outcome` memory lines stay as
  they are - they feed the timeline display, not the model. This also ends the §9ar class of error for lessons: an
  errand run produces candidates the person dismisses, nothing lands silently.

### 5.2 Gate

- After a run: a "Lessons" panel next to Artifacts (desktop) and a page next to Proposals (server - same pattern as
  `ProposalsScreen.razor`/`ServerStaging`). Each candidate is shown as written, with keep / dismiss / edit and a
  scope switch (default Workspace = AI-DLC's "narrowest by default"); a free-text "anything for next time" field.
- Headless and scheduled runs: candidates wait; an Inbox item of kind `lesson` points to them.
- "Promote to global" copies a confirmed workspace rule to the global layer (template Workspace->Global precedent).
- Before a write to Workspace, an advisory consistency check against Global rules (one strong-model call, only when
  Global has rules under the same heading); a contradiction is shown with keep / revise / skip. It is an aid, not a
  boundary - the store accepts what the person confirms.

### 5.3 Injection

- Not through `DefaultWorkers.Augment`: it is called once per host (`Settings/EngineComposition.cs:284`), not per run
  or workspace, and its result becomes the system prompt (`Orchestrator.cs:1430`), i.e. the cached prefix
  (`APrefixCacheThatBreaksIsToldTests`). Workspace rules there would need workers rebuilt per run and would break the
  prefix cache whenever a rule changes. Instead the run carries a `## Workspace rules` block (Global rules before
  workspace ones; strict-additive, nothing overrides), placed after the stable prefix.
- This does **not** revive the history block disabled at `Orchestrator.cs:4590-4611`: rules are short standing
  instructions a person confirmed, not past outcomes the model might reuse stale.
- Compiled once in `ContextProvider.BuildAsync`/`RunComposer`; persist the full effective rule text, ordering and
  source metadata in the run record and checkpoint, alongside a rule-set hash in `RunSettings` (`Core/History.cs:29`).
  Resume uses the saved text even if the rules were edited or deleted. Rules confirmed during a run apply to the
  next new run, not to a resumed continuation. A hash is an identifier, not a recoverable instruction snapshot.
- A size cap with the reason in the code: each rule costs every worker turn's context.

### 5.4 Lesson -> check

- A candidate of kind "recurring check" (the person ticks "make this a check") scaffolds a Phase 3 manifest in
  `.enactive/checks/` with the command left for the person to fill in - the scaffold never invents a command.
  AI-DLC's `SENSOR_PROPOSED`.

### 5.5 Rules UI

- Workspace card and Settings: list, edit, delete confirmed rules, with the run each came from.

### Tests

- A run with a review rejection produces a candidate; nothing reaches the worker prompt until it is confirmed.
- A rule confirmed during a run does not appear in that run's prompts and appears in the next one.
- `RunSettings` carries the rule-set hash; history and checkpoint retain the full effective text.
- Edit/delete/confirm rules after a checkpoint: resume uses the saved text; a new run uses the updated confirmed set.
- Dismissed candidates do not come back for the same source.

### Measurement

Two bench runs of the same scenario with one confirmed rule that addresses the first run's rejection; the second run
should not repeat it. This is a measurement, not a guarantee - report it as such.

---

## What is deliberately not taken

- AI-DLC's methodology (phases, personas, scope profiles). The nearest Enactive notion, execution profiles, exists only
  as a non-goal in `Docs/TOOL_CATALOG_PLAN.md:30`; there is no `ExecutionProfile` in code.
- Their conductor as an LLM following a prose `SKILL.md`. In Enactive the engine calls the workers; a cheap model is
  never asked to carry out a directive by itself.
- Write-time blocking sensors (AI-DLC itself keeps them advisory).
- Human approval gates between every step. Enactive's autonomy levels already decide that; a pause is the voluntary
  version.

## Risks

- **Model calls**: Phase 1's gate and Phase 2's extra nudges add turns; the bench reports tokens next to outcomes.
- **Coverage semantics**: `mentioned` proves only mention; exclusions require review, and an ordering-only declaration
  must agree with the request. Structural completeness alone does not prove the task was done.
- **Deferred checks**: wave/apply checks must survive pauses and cannot be presented as passes before execution.
- **Refactor regression in Phase 4**: 4.1 is deferred until ownership and failure recovery are designed, then protected
  by 4.0 and fault-injection tests. Pause/resume does not depend on it.
- **Rule drift**: confirmed rules accumulate; the UI shows source runs and the cap forces pruning.

## Open questions for the owner

1. Phase 1.5: turn `StepOutputs` + `TypedCriteria` on by default after the bench, or keep them opt-in?
2. Phase 2: who picks the new nudge limits - the bench numbers alone, or a fixed ceiling as well?
3. Phase 3: is a global (machine-wide) check layer wanted, or workspace only?
4. Deferred Phase 4.1: who exclusively owns state, and what is the commit/recovery protocol when persistence fails?
5. Phase 5: is "Global" enough as the broader layer, or is a per-team layer (server, several operators) needed?
6. Phase 3: where do a staged run's checks run, given that apply is per file, by a person, after the run ended -
   a manual "Run checks now", automatic on an atomic "apply all", or an isolated copy before apply?
