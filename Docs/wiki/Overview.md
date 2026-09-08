# Application Overview

[Wiki home](README.md)

## Purpose

Enactive makes AI-assisted work actionable and reviewable inside a project folder. It combines a natural-language command interface with explicit workers, model routing, tool permissions, execution records, and optional checks of the result.

A typical request is “Find the cause of this build failure,” “Update this document to match the code,” or “Fix this bug and run the tests.” The application translates that request into either a quick action or a dependency-aware plan, executes tools, and records the outcome.

The main product value is control over execution: which model performs each responsibility, what tools a worker can use, which actions require a decision, and what evidence supports the final report.

## Main use cases

| Use case | Suitable entry point | Typical worker |
| --- | --- | --- |
| Investigate unfamiliar code | Manual request | Reviewer or Developer |
| Implement a focused change | Manual request or Fix Bug template | Developer |
| Diagnose a build failure | Investigate Build Failure template | Developer or Ops |
| Maintain technical documentation | Documentation Sync template | Writer |
| Build and test a project | Release Check template | Developer or Ops |
| Add meaningful tests | Improve Test Coverage template | Developer |
| Repeat a standard workflow | A custom template | A worker whose tools match the task |
| Run without an open desktop window | Console template invocation | Built-in worker selected by the template |

Template names express intent, not a complete security boundary. For example, a “changes nothing” task that runs a build may still create build outputs through the shell.

## Product surfaces

### Desktop application

The Avalonia desktop UI is the richest host. It provides:

- Workspace selection and remembered per-workspace run preferences.
- A command bar, role picker, autonomy slider, and Stage changes option.
- A template library and a separate template editor under Settings.
- Execution cards, tool results, artifacts, approval cards, and status metrics.
- Routing information showing the models used by phases and individual steps.
- Run history, attempts, timeline, Inbox, environment information, and logging.
- Provider, worker, phase, and MCP configuration.

### Console host

The console host executes one task and exits, or prints timeline/Inbox data. It supports interactive free-text tasks and unattended template tasks. It uses its own environment-based model configuration and built-in workers. See [Console](Console.md) before transferring a desktop workflow into a scheduled job.

### Remote server preview

The separate `server/` application implements a gateway and a browser interface for remote tasks, host registration, queues, decisions, and results. A production host bridge connecting it to the local Enactive engine is still missing. See [Remote preview](Operations.md#remote-server-preview).

## Core concepts

### Workspace

A workspace is an existing folder selected in the desktop app. It supplies the execution root and identifies stored project history. It may be a Git repository, but Git is not a requirement for every task.

The app remembers the workspace's autonomy selection, worker selection, and staging preference. These are distinct from the globally configured provider and worker definitions.

Workspace identity is derived from the normalized folder path. Moving a folder changes that identity; this matters especially for centrally stored data and remembered approvals.

### Task and run

A task expresses an outcome. A run is one attempt. A new command-bar submission creates a new task; Retry and Run again preserve the previous task identity and record another attempt.

Each run can carry its request, events, decisions, artifacts, token usage, settings summary, and a frozen template specification. Historical records explain what happened without consulting today's template contents.

### Worker and model

A worker is a role. Its instructions and tool allowlist define the work it can perform; its model policy selects the model it normally uses. Several workers may use the same model. Different phases may use different models while still serving one selected worker.

The built-in **Reviewer worker** is a read-only investigator. The **Review phase** is a separate evaluator of another worker's output. Selecting the Reviewer worker does not enable the Review phase.

### Template and resolved specification

A template stores a goal, parameter definitions, optional worker ID, permission restrictions, checks, and execution limits. At launch, its parameters are filled and its restrictions are combined with workspace policy into a resolved specification.

A template does not store a fixed execution graph. Planning still happens on every run.

## What “completed” means

The model's final sentence is only one input to completion. The engine also considers tool failures, stalled execution, reviewer verdicts when Review is configured, and required success criteria when present.

| Outcome | Interpretation |
| --- | --- |
| Completed | The run met the completion conditions available to it |
| Failed | Execution/review or a required check established failure |
| Incomplete | Work or required verification could not finish, for example because approval was denied or a limit was reached |
| Cancelled | Execution was cancelled |

A Completed run with no deterministic checks is not proof that a build or test suite passed. Inspect both the outcome and the checks. Likewise, a recorded file path is not a promise that the file remains unchanged after the run.

## Implementation references

- [Desktop layout](../src/Enactive.App.Ui/MainWindow.axaml)
- [Desktop composition and run lifecycle](../src/Enactive.App.Ui/MainWindow.axaml.cs)
- [Workers](../src/Enactive.Agents/DefaultWorkers.cs)
- [Task templates](../src/Enactive.Core/Templates.cs)
- [Run report](../src/Enactive.Core/RunReport.cs)
