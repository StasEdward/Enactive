# A command's change held to the request's limit - implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** a shell, git, docker or MCP call that changes a file the request says not to change is refused after the
fact, and the file is put back byte for byte where git can prove the bytes - otherwise named.

**Architecture:** in a step under the request's change limit that the engine measures, the tool loop takes a workspace
snapshot before and after each call of a tool that declares no paths; `CommandEffect` selects the files the run found
that the call changed, asks `ChangeLimitGuard` once, has `IWorkspaceChanges.PutBackAsync` (`ExactRestore`) put
refused ones back, and returns the text appended to the call's result and the events to record.

**Tech Stack:** C# / .NET 10, xUnit, git CLI through `AutomaticGit`.

**Spec:** `plans/aidlc-adoption-plan.md`, "Bench follow-ups" -> "3. A forbidden file rewritten through a shell
command" -> "Design - decided 2026-10-28".

## Global Constraints

- `AGENTS.md`: a general rule, nothing for one scenario, language or layout; every change with a test that fails
  without it; a limit, rule or workaround carries a comment on what went wrong without it.
- Put back only byte for byte; never write bytes that were not there. Not provable -> named, not put back.
- Applies only when all three hold: the step has a `ChangeLimitGuard`; `frame.Changes` and `frame.StepStart` are not
  null; `WriteBoundary.Unchecked(definition)` is true. Anywhere else: no snapshot, no question, behaviour as today.
- Git is always run through `AutomaticGit` (repository config and attributes only, no global/system ones).
- Comments and messages follow the codebase's voice: plain sentences, the run or bench that showed why.
- Tests: `dotnet test tests/Enactive.Engine.Tests -nologo --filter "FullyQualifiedName~<Class>"`.

## Review Focus

1. A command that changes `.gitattributes` together with a forbidden file: nothing is put back (attributes no longer
   those of the snapshot) - test in Task 1.
2. A command that changes many files the run found (a formatter): one question for the call, every file named in the
   payload, the step's text names up to ten and counts the rest - test in Task 3.
3. A command that fails (non-zero exit) after writing: still checked; the refusal text goes with its failure - test in
   Task 4.
4. A binary file (bytes that are not UTF-8) rewritten by a command: put back exactly - test in Task 1.
5. A refused file that the command deleted: written back whole - test in Task 1.

---

### Task 1: Put a file back from the snapshot, byte for byte

**Files:**
- Create: `src/Enactive.Workspace/ExactRestore.cs`
- Modify: `src/Enactive.Workspace/AutomaticGit.cs` (a run whose stdout goes to a file)
- Modify: `src/Enactive.Core/WorkspaceChanges.cs` (interface method + `PutBack` record)
- Modify: `src/Enactive.Workspace/WorkspaceChanges.cs` (implement it)
- Create: `tests/Enactive.Engine.Tests/TestGit.cs` (one shared helper; three test files keep private copies today)
- Test: `tests/Enactive.Engine.Tests/AFileIsPutBackOnlyByteForByteTests.cs`

**Interfaces:**
- Produces, in `Enactive.Core.Artifacts`:
  - `public sealed record PutBack(bool Done, string? Why)` with `static PutBack Back` = `(true, null)` and
    `static PutBack Left(string why)`.
  - On `IWorkspaceChanges`: `Task<PutBack> PutBackAsync(WorkspaceSnapshot before, WorkspaceSnapshot after,
    string path, IReadOnlyCollection<string> changedWithIt, CancellationToken ct)` - default implementation
    `Task.FromResult(PutBack.Left(PutBackReasons.NotGit))`. `changedWithIt` = every path the call changed (for the
    `.gitattributes` rule).
  - `public static class PutBackReasons` with these exact strings: `NotGit` = "not a git workspace";
    `Conversion` = "a line-ending conversion applies to it"; `ChangedAgain` = "it changed again after the command";
    `NotAFile` = "it is not a regular file"; `Attributes` = "the command changed a .gitattributes";
    `NotExact` = "its bytes could not be written back exactly".
- Produces, in `Enactive.Workspace`: `AutomaticGit.RunToFileAsync(string root, IReadOnlyList<string> args,
  string destination, CancellationToken ct) -> Task<int>` (exit code; same config guard as `RunAsync`, stdout as
  bytes to `destination`).

- [ ] **Step 1: Write the failing tests** (`AFileIsPutBackOnlyByteForByteTests`, each on a temporary repo made with
  `TestGit.Repository(root)` = init, user, `core.autocrlf false`, commit; snapshots through `new WorkspaceChanges(root)`):
  - `A_file_a_command_rewrote_is_put_back_byte_for_byte`: content `"a\r\nb\n\0\xff"` as bytes; before-snapshot;
    overwrite; after-snapshot; `PutBackAsync` -> `Done`; `File.ReadAllBytes` equals the original.
  - `A_file_a_command_deleted_is_written_back_whole`: delete between snapshots -> `Done`, bytes equal.
  - `Where_the_repository_converts_line_endings_nothing_is_written`: `git config core.autocrlf true` before the
    snapshots -> `Left(PutBackReasons.Conversion)`, file still holds the command's bytes.
  - `A_text_attribute_is_a_conversion_too`: `.gitattributes` `* text=auto` committed -> `Left(Conversion)`.
  - `A_command_that_changed_gitattributes_puts_nothing_back`: `changedWithIt` contains `.gitattributes` ->
    `Left(PutBackReasons.Attributes)`.
  - `A_file_changed_again_after_the_command_is_left`: write once more after the after-snapshot ->
    `Left(PutBackReasons.ChangedAgain)`, file holds the latest bytes.
  - `Outside_git_nothing_is_put_back`: plain folder -> `Left(PutBackReasons.NotGit)`.
  - `Only_a_regular_file_mode_is_written` (unit): `ExactRestore.Regular("100644")` and `("100755")` true;
    `("120000")`, `("160000")` false.
  - `Bytes_that_do_not_hash_to_the_blob_are_not_moved_into_place` (unit): `ExactRestore.VerifiedAsync(root,
    tempPath, blobId: "0000000000000000000000000000000000000000")` false and the target untouched.

- [ ] **Step 2: Run them - FAIL** (no `PutBackAsync`, no `ExactRestore`).
  Run: `dotnet test tests/Enactive.Engine.Tests -nologo --filter "FullyQualifiedName~AFileIsPutBackOnlyByteForByte"`

- [ ] **Step 3: Implement.** `ExactRestore` (internal static, Workspace):
  `PutBackAsync(string root, string beforeTree, string afterTree, string path, IReadOnlyCollection<string> changedWithIt, CancellationToken ct) -> Task<PutBack>`,
  `Regular(string mode) -> bool`, `VerifiedAsync(string root, string file, string blobId, CancellationToken ct) -> Task<bool>`.
  Order: `.gitattributes` in `changedWithIt` -> `Attributes`; `config --get core.autocrlf` not empty/`false` ->
  `Conversion`; `check-attr -z text eol filter ident working-tree-encoding -- <path>`: any value other than
  `unspecified`, or `text` other than `unspecified`/`unset` -> `Conversion`; `ls-tree -z <after> -- <path>` blob vs
  `hash-object --no-filters -- <path>` of the file now (absent on both sides is equal) -> else `ChangedAgain`;
  `ls-tree -z <before> -- <path>`: missing or not `Regular` -> `NotAFile`; `RunToFileAsync cat-file blob` into
  `<dir>/.<name>.enactive-putback` -> `VerifiedAsync` -> `File.Move(overwrite: true)`, else delete temp and
  `NotExact`. Create the directory for a deleted file. `WorkspaceChanges.PutBackAsync`: snapshots without a `Tree`
  -> `NotGit`, else `ExactRestore`. Comment at the top of `ExactRestore`: the 2026-10-18 bench run and the owner's
  "never write what was not there".

- [ ] **Step 4: Run them - PASS.** Same command; then the existing `WorkspaceChanges` tests:
  `--filter "FullyQualifiedName~WorkspaceChanges"`.

- [ ] **Step 5: Commit** - "A file a command changed can be put back from the snapshot, byte for byte or not at all".

### Task 2: The guard's question after the fact

**Files:**
- Modify: `src/Enactive.Agents/ChangeLimitGuard.cs`
- Test: `tests/Enactive.Engine.Tests/AChangeTheRequestLimitsIsJudgedAsItIsMadeTests.cs` (new section "after a command")

**Interfaces:**
- Consumes: `FileChange` (Core).
- Produces: `internal sealed record AfterTheFact(IReadOnlyList<string> Refused, string? Reason, TokenUsage Usage,
  string? Note)` nested in `ChangeLimitGuard`, and
  `Task<AfterTheFact> CheckMadeAsync(int? stepNo, string? stepTitle, string? saidByStep, ToolCall call,
  IReadOnlyList<FileChange> made, CancellationToken ct)`. `made` holds only files to ask about (Task 3 selects them);
  a deleted file or a rename's old path counts as a removal for the allowance key.

- [ ] **Step 1: Write the failing tests** (reuse the file's `Guard`, `Request`, `Limit`):
  - `A_command_s_change_is_refused_after_the_fact`: planner answers `{"allow":false,...}` -> `Refused` = `["prices.txt"]`;
    the question contains the command's arguments and the diff text passed in `FileChange.Diff`.
  - `A_command_s_change_the_request_allows_stands`: `allow:true` -> `Refused` empty.
  - `A_file_allowed_by_an_edit_is_not_asked_about_after_a_command`: `CheckAsync` allowed `orders.txt` in step 1,
    then `CheckMadeAsync` with `orders.txt` -> no new request.
  - `Refused_three_times_a_command_s_change_is_refused_without_asking`: after `AsksPerFile` refusals through
    `CheckAsync`, `CheckMadeAsync` -> `Refused` = that file, request count unchanged, `Reason` contains "not asked again".
  - `Without_an_answer_a_command_s_change_stands_and_is_said`: "I cannot say." -> `Refused` empty, `Note` contains
    "went ahead unchecked".

- [ ] **Step 2: Run them - FAIL.** `--filter "FullyQualifiedName~AChangeTheRequestLimitsIsJudgedAsItIsMade"`

- [ ] **Step 3: Implement `CheckMadeAsync`** with the same `_allowed`/`_refused` keys as `CheckAsync`. A new
  instruction constant beside `Instruction`, starting "A command a step of a plan ran has changed files that were in
  the workspace before the run." and ending with the same JSON contract. The question: request, limits, step, what
  the step said, `The command: {call.Name} {Cut(args)}`, then per file its kind and `Cut(diff)` or "(no diff: the
  workspace is not under git)".

- [ ] **Step 4: Run them - PASS** (the whole class, old tests included).

- [ ] **Step 5: Commit** - "The request's limit can be asked about a change a command already made".

### Task 3: What a command changed, decided and put into words

**Files:**
- Create: `src/Enactive.Agents/CommandEffect.cs`
- Test: `tests/Enactive.Engine.Tests/WhatACommandChangedIsHeldToTheLimitTests.cs` (unit part)

**Interfaces:**
- Consumes: `ChangeLimitGuard.CheckMadeAsync` (Task 2); `IWorkspaceChanges.TakeAsync/CompareAsync/PutBackAsync`
  (Task 1); `IArtifactScope.BeforeRunAsync`; `WorkEventPayload.RevertPayload(int?, IReadOnlyList<string>,
  IReadOnlyList<string>)` (`Enactive.Core/Events.cs`).
- Produces: `internal sealed class CommandEffect(ChangeLimitGuard guard, StepFrame frame, string? stepTitle)` with
  - `static bool Applies(ChangeLimitGuard? guard, StepFrame frame, ToolDefinition? definition)`;
  - `Task<WorkspaceSnapshot?> BeforeAsync(CancellationToken ct)`;
  - `Task<Outcome> AfterAsync(ToolCall call, WorkspaceSnapshot before, string? saidByStep, CancellationToken ct)`;
  - `internal sealed record Outcome(string? Appended, IReadOnlyList<WorkEvent> Events)` - `Appended` null when
    nothing was refused.
  - `internal static IReadOnlyList<FileChange> Asked(...)` is not public API; selection is tested through
    `AfterAsync`.

- [ ] **Step 1: Write the failing tests** (git workspace via `TestGit`, `DiskArtifactStore` step scope, a frame
  built as `StepFrame` is in existing unit tests, a `FakeChatProvider` planner):
  - `Applies_only_with_a_limit_a_measured_step_and_a_tool_that_names_no_paths`: false for no guard, for
    `frame.StepStart == null`, for `write_file`; true for a definition with `WorkspaceEffect` set and no
    `ChangedPathArguments`.
  - `A_refused_change_is_put_back_and_said`: `Appended` starts "REFUSED after the fact: this command changed
    'prices.txt'" and contains "It was put back as it was before the command."; one `ArtifactReverted` event,
    `RevertedPaths()` = `["prices.txt"]`; file bytes as before.
  - `A_refused_change_that_cannot_be_put_back_says_why`: autocrlf on -> `Appended` contains "It could not be put
    back (a line-ending conversion applies to it)" and "Put it back yourself"; `KeptPaths()` = `["prices.txt"]`.
  - `What_the_command_added_or_the_run_made_is_not_asked`: a new file, a file the store created, `.enactive/scratch/x`
    -> planner has no requests, `Appended` null, no events.
  - `A_rename_is_asked_by_the_name_it_left`: the command renames `prices.txt` to `old-prices.txt`, refused -> the
    question names `prices.txt`; `prices.txt` is back byte for byte; `old-prices.txt` stays and is in `KeptPaths()`
    and in `Appended` (made by the command, not the run's to delete).
  - `Many_files_are_named_up_to_ten_and_counted`: 12 refused files -> `Appended` names ten and says "and 2 more";
    the event payload lists all 12.

- [ ] **Step 2: Run them - FAIL.** `--filter "FullyQualifiedName~WhatACommandChangedIsHeldToTheLimit"`

- [ ] **Step 3: Implement.** Selection: `CompareAsync(before, after)`; keep `Modified`, `Deleted`, `Renamed` (ask by
  `OldPath`); drop paths under `WorkspaceGuard.ReservedFolder` and `WorkspaceGuard.ScratchPrefix`; drop
  `BeforeRunAsync(path).State == Absent`. Nothing left -> `Outcome(null, [])`. Otherwise `CheckMadeAsync`; usage
  event via `guard.UsageEvent`; `Note` -> `ErrorObserved` event; for each refused path `PutBackAsync(before, after,
  path, allChangedPaths)`. Text exactly as the spec: "REFUSED after the fact: this command changed '{paths}', which
  the request limits ({quoted limits}) - {reason}." then "It was put back as it was before the command." for those
  put back and "'{path}' could not be put back ({why}). Put it back yourself, or hand on why the step cannot be done
  without changing it." for each left. `ArtifactReverted` summary "[{step}] Put back after a command: ..." / "Left
  after a command: ... - {why}", payload `RevertPayload(stepNo, putBack, left)`.

- [ ] **Step 4: Run them - PASS.**

- [ ] **Step 5: Commit** - "What a command changed is selected, asked about and put back or named".

### Task 4: The tool loop measures around such a call

**Files:**
- Modify: `src/Enactive.Agents/Orchestrator.cs` (step tool loop: where `ToolInvocation.ExecuteAsync` runs, ~4918,
  before `accounting.Record`; the loop's method receives `changeLimits` and `stepTitle` at ~3830)
- Test: `tests/Enactive.Engine.Tests/WhatACommandChangedIsHeldToTheLimitTests.cs` (run part)

**Interfaces:**
- Consumes: `CommandEffect` (Task 3).

- [ ] **Step 1: Write the failing tests** - through `fx.RunAsync`, shaped as
  `In_a_run_the_change_is_refused_when_it_is_made` in `AChangeTheRequestLimitsIsJudgedAsItIsMadeTests` (planner
  answers the contract with `change_limits`, and the guard question by content), workspace under `TestGit`:
  - `A_command_that_rewrites_a_forbidden_file_is_refused_and_the_file_put_back` (THE ONE THAT MATTERS):
    worker `run_command` `echo 2.5> prices.txt`; then `prices.txt` bytes as committed; a `ToolResult` event
    `run_command -> ok:` containing "REFUSED after the fact"; an `ArtifactReverted` event.
  - `A_command_s_change_the_request_allows_stays`: `echo 2 apples: 6> orders.txt` -> stays.
  - `Outside_git_the_file_is_named_and_the_step_told_why`: no `TestGit` -> file keeps the command's content; result
    contains "could not be put back (not a git workspace)".
  - `A_command_that_fails_after_writing_is_still_held`: `echo x> prices.txt & exit 3` -> failed result still
    carries the refusal; file put back.
  - `A_step_without_a_limit_takes_no_snapshots`: contract without `change_limits`; assert the planner got no guard
    question and the command's change stays.

- [ ] **Step 2: Run them - FAIL.**

- [ ] **Step 3: Wire it.** Build `CommandEffect` once per step when `changeLimits` is not null. Before
  `ToolInvocation.ExecuteAsync`: `CommandEffect.Applies(...)` -> `BeforeAsync`. After it, before
  `accounting.Record`: `AfterAsync(call, before, lastAssistantText)`; append `Appended` to `invocation.Value.Output`
  (on a new line); `yield return` its events after the call's `ToolResult` event. Reads run in parallel are never
  `Unchecked`, so the parallel-read path is untouched; say so in a comment.

- [ ] **Step 4: Run them, then the neighbours - PASS.** This class, `AChangeTheRequestLimitsIsJudgedAsItIsMade`,
  `AStepSentBackThatEndsUnfinishedIsPutBack`, `RefusedByGit`, then the whole suite
  `dotnet test tests/Enactive.Engine.Tests -nologo`.

- [ ] **Step 5: Commit** - "A command's change is held to the request's limit as it is made".

### Task 5: Measure on the bench and record it

**Files:**
- Modify: `plans/aidlc-adoption-bench.md`, `plans/aidlc-adoption-plan.md` (follow-up 3 status)

- [ ] **Step 1:** `dotnet run --project tools/Enactive.Bench -- --solutions` with `DOTNET_ROLL_FORWARD=Major` - every
  truth holds.
- [ ] **Step 2:** three runs of `build-error` with `%APPDATA%/Enactive/checks/build.json` =
  `{"id":"build","fireOn":"Step","severity":"Blocking"}` (remove it afterwards):
  `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Enactive.Bench -- --only build-error`. Record per run: verdict,
  whether "REFUSED after the fact" appears in `console.log`, `Report.cs` changed or not.
- [ ] **Step 3:** one ordinary run of every scenario; compare tokens and verdicts with the 2026-10-28 runs.
- [ ] **Step 4:** write the numbers into `aidlc-adoption-bench.md` as a measurement; set follow-up 3 to "Fixed" or
  "Built, measured" with the commits in the plan's table.
- [ ] **Step 5: Commit** - "Record the bench for a command's change held to the limit".
