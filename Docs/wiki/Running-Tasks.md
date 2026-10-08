# Running Tasks in the Desktop App

[Wiki home](README.md)

## Manual mode: write a task directly

Manual mode means entering an individual request in the command bar. Enactive still plans and executes the request; “manual” describes how you define and supervise the task.

### Launch procedure

1. **Select the workspace.** Verify the actual folder, especially when switching projects. Autonomy, worker selection, and staging preferences follow the workspace.
2. **Choose a worker.** Use Developer for implementation and tests, Reviewer for read-only investigation, Writer for documentation, and Ops for command-oriented diagnostics.
3. **Set autonomy.** Choose the level appropriate to the tools you expect to allow automatically.
4. **Choose whether to stage changes.** This applies to supported built-in file operations, independently of the autonomy slider.
5. **Describe the outcome.** Include the problem, scope, restrictions, and a way to check completion.
6. **Press Run or Enter.** Watch Execution and respond to decision cards when needed.
7. **Inspect the result.** Read the terminal outcome, review/check results, changed files, and any failures or denied operations.

### Command-bar shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+K` | Focus the command bar and select its text |
| `Enter` | Start the request |
| `Ctrl+Enter` | Insert a newline |

### Example: a focused implementation request

```text
Fix the failure when an empty search query is submitted.

Scope: the search service and its existing tests.
First inspect the current behavior, then make the smallest useful change.
Preserve the public API. Do not commit or push.
Run the relevant tests and report the command and result.
```

A test command written in a manual prompt instructs the worker. It does not create a separate deterministic success criterion in the host. Use a template with SuccessCriteria when you want the engine to run that check independently at the end.

## Autonomy and approval

| Level | Automatic behavior in the current policy |
| --- | --- |
| Observe | Tools requiring Observe can run; higher-level actions ask if the role permits them |
| Suggest | Tools up to Suggest can run; higher-level actions ask |
| Execute | File operations within the level can run; `run_command`, `run_powershell`, `git`, and `docker` explicitly ask |
| Autonomous | No default AskBefore list; eligible tools within the effective level run automatically |

The effective level is narrowed by the worker's level and any template ceiling. A worker's tool allowlist is a separate gate. Increasing autonomy cannot make an unlisted tool available to that worker.

An above-level action generally asks for one-off approval; the level is not the same as an absolute deny. A template's explicit Deny entry is absolute in the permission engine. The Suggest label also does not turn on staging: use **Stage changes** explicitly when you want a diff/apply workflow.

### Decision cards

Read the full action details. The card can offer a one-time approval, a session approval, or a remembered workspace approval for eligible tools. Remembered approvals are scoped to tool names, not a promise that only one particular command string will ever run.

There are two remembered workspace approvals, and they grant different things:

| Button | Holds for |
| --- | --- |
| **Allow (workspace)** | Runs somebody is watching: the window's command bar, the console without `--approve` |
| **Allow (workspace, unwatched runs too)** | Those, and runs nobody is watching: background runs and scheduled runs |

A run nobody is watching is offered a tool that asks only when it has the second kind of approval; every other tool that asks is kept back from it. An approval given before the second button existed is the first kind. A shell is never remembered either way, and a task from a phone is always asked afresh.

Workspace approvals are stored in `%APPDATA%\Enactive\permissions.json`, keyed by workspace identity. The legacy `<workspace>\.enactive\permissions.json` is ignored.

An MCP server can require approval for every call; those calls cannot be silently authorized through a remembered decision.

## Launch a task from Templates

1. Select the intended workspace and autonomy level.
2. Open **Templates** from the main window.
3. Select an entry and inspect its origin: built-in, global, or this workspace.
4. Fill the generated parameter form. Required/type errors appear before Run becomes available.
5. Read the goal and summary of restrictions, checks, and limits.
6. Press **Run**. The library resolves the template and starts a foreground task.

A template's `WorkerId`, when provided and recognized, overrides the role picker. If the template does not specify a worker, the desktop uses the selected worker. An unknown worker ID currently falls back to the host's default worker; it is not a launch validation error. Keep IDs synchronized with Team settings.

The resolved specification is recorded with the run. Editing the template later does not rewrite that historical specification.

**Current limitation:** the template's displayed “reviewed”/“not reviewed” metadata comes from `ReviewRequired`, but runtime Review is controlled by the phase binding. Configure the Review model explicitly; the template flag neither enforces nor disables it.

See [Templates](Templates.md) for authoring and scope behavior.

## Staging, applying, and rejecting files

With **Stage changes** enabled, supported built-in file writes become text proposals. In Artifacts, choose **Diff** to inspect a proposal, then **Apply** or **Reject**.

Staging is not an isolated checkout or an all-effects transaction:

- It represents text changes; operations requiring deletion or binary content may be refused.
- Shell commands and MCP tools can change real files outside staging.
- A shell build/check reads the actual filesystem and may not see unapplied proposed edits.
- Background launches with staging enabled are refused.

Use staging for document or file-edit review. For an implementation task requiring builds against changed files, account for the fact that shell verification needs those changes on disk.

With staging off, built-in writes go directly to the workspace. The artifact journal supports scoped revert/undo for tracked writes. If another step or the user changed the file afterward, revert reports the conflict instead of overwriting the newer content.

## Read the execution screen

| Area | How to use it |
| --- | --- |
| Execution | Inspect step progress, tool calls, returned evidence, review feedback, and skipped dependencies |
| Artifacts | Open output files, inspect staged differences, and use the available apply/reject/undo actions |
| Log | Read the run's detailed event flow |
| Routing | See which model actually served the worker, phases, and routed steps |
| AI status | Monitor usage, tool calls, elapsed time, and current activity |
| History | Open prior runs and inspect their recorded attempts |

The Routing panel is especially useful when a local worker seems unexpectedly expensive: complex steps may have been sent to Execute heavy.

## Background mode

**Run in background** starts command-bar work inside the desktop process and frees the foreground interface. Results and declined decisions are recorded in the workspace Inbox.

If a tool requires a decision, the background handler declines it and records the need for approval. Re-run interactively to approve; this is not a suspended conversation waiting for an Inbox answer.

Background work depends on the desktop process remaining alive. Closing to the tray keeps it alive; exiting the application ends that process. Background mode is not a durable scheduler.

The template library currently launches foreground runs. Copying a resolved goal into the command bar and using background mode does not preserve the template's checks, limits, or frozen specification.

## Stop and inspect partial work

Use **Stop** to request cancellation of a foreground run. Cancellation does not imply automatic rollback of every previous effect. Inspect artifacts and the final recorded events before restarting.

## Retry versus Run again

| Action | Inputs reused | What can differ |
| --- | --- | --- |
| Retry | The previous resolved specification, or original request for a manual task | Current files, provider configuration, model behavior, and runtime composition |
| Run again | Today's template resolved with the old parameter answers and current workspace policy | Updated goal, restrictions, checks, limits, and template version |

Both actions create a new attempt under the same task identity. Neither resumes execution at the last successful step — for that, see Resume below.

Retry is not a bit-for-bit replay: the frozen template specification does not freeze provider settings or workspace contents. It reuses the stored permission policy, so inspect the old specification when your policy expectations have changed.

If Run again encounters a new required parameter or a now-invalid value, it reports the problem and directs you to fill the template form again. Deleting a template does not erase previous run snapshots.

## Resume an interrupted run

A run whose process went away before it finished — the app was closed, the machine restarted, a build agent was reclaimed — appears under **UNFINISHED** above the run history, with the request, when it stopped, and how many of its steps were done. Pressing **Resume** runs the steps that are left. The console equivalent is `--resume [<run id>]`, which picks up the most recently interrupted run in the workspace when no id is given.

What Resume is, precisely:

- It carries on **from the last completed step**, not from where the run was in the middle of one. A step that was in progress when the process went away is **done again from its beginning**. The files that step had already written are still in the workspace, so it starts again in a folder it has already changed.
- It does **not** re-plan. The plan, the steps that finished, what they concluded and the files they produced all come back.
- It runs under the **permissions and role the interrupted run started with**, not whatever the sliders are set to now.
- It is recorded as a **new attempt under the same task**, so the history shows two rows for one piece of work.
- A run stopped at an approval will **ask again** when it is resumed. Approvals are not queued while the app is closed.

What is never offered:

- A run that reached an end — completed, failed or cancelled — is not resumable. It ran to a conclusion. Use Retry or Run again instead.
- A run with **Stage changes** on is never checkpointed, so it cannot be resumed. Staged proposals are held in memory, and resuming would apply later steps on top of earlier ones that were never written.
- A quick action has no step boundary inside it and leaves nothing to resume.

Checkpoints live in `<workspace>/.enactive/checkpoints`, one file per run, and are deleted when a run reaches its end. They stay on the machine the run was interrupted on even when the run history is in MySQL — a half-changed folder is not portable.

## Implementation references

- [Launch, approvals, staging, and replay actions](../src/Enactive.App.Ui/MainWindow.axaml.cs)
- [Checkpoint and resume](../src/Enactive.Core/Checkpoints.cs)
- [Template launch form](../src/Enactive.App.Ui/ViewModels/TemplatesViewModel.cs)
- [Approval persistence](../src/Enactive.App.Ui/ApprovalStore.cs)
- [Permission engine](../src/Enactive.Agents/PermissionEngine.cs)
- [Background decision handler](../src/Enactive.Agents/BackgroundDecisionHandler.cs)
