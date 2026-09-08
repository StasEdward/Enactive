# Enactive

A desktop environment for AI agents (a "GUI for AI agents"), built on **.NET 10**.
Site: [enactive.dev](https://enactive.dev). Formerly named *AIClient* — renamed to **Enactive** in 2026-09;
namespaces, assemblies, env vars and on-disk data folders all use the new name (see *Naming / migration* below).
See `PLAN.md` (vision) and `PLAN_v2.md` (development spec — its §11 is the honest status: what is built and what is not).
`TASK_TEMPLATES_PLAN.md` is the saved-task system (complete); `FIX_PLAN.md` is the defect log — every fix since the
first review, with the log line that exposed it, what was changed, what was deliberately not, and the test that fails
without it; its §9 tail is the open backlog. `LOGGING.md` documents the global log and the log analysis; `MODELS.md`
the multi-provider team-of-models design (implemented); `STYLING.md` how the Avalonia/Fluent UI is themed — read it
before changing anything visual.

The engine turns one intent into real, reviewable, recorded action:

```
Command -> Intent -> Context(+Environment) -> Planner -> Orchestrator -> Worker(role)
        -> Provider -> Tool -> ToolResult -> Artifact -> Event -> RunRecorder (persist + memory)
```

## Solution

`Enactive.sln`, **8 projects** (`net10.0`, pinned via `global.json`) plus `tests/Enactive.Engine.Tests` (762 tests)
and `tests/Enactive.Mcp.TestServer`. The engine is dependency-light: `Core`/`Providers`/`Tools`/`Agents`/`App.Console`
use **zero external NuGet packages**; only `Workspace` (Microsoft.Data.Sqlite, SQLitePCLRaw, MySqlConnector), `Secrets`
(ProtectedData) and `App.Ui` (Avalonia ×3) pull anything in. `Enactive.Agents` references **only** `Core` — a constant
both the engine and a tool need (`ToolArguments.ExpectedExitCodes`) lives in `Core.Tools`, not in `Tools`.

| Project | Responsibility |
|---|---|
| `Enactive.Core` | Domain model + abstractions only (Intent, WorkContext, Environment, Plan/PlanStep (DAG), Worker/ModelPolicy, Decision, Permissions, Memory, Inbox, events, diagnostics, task templates and their resolution, the `ExecutionJournal` a step's evidence is kept in, the built-in templates). No transport/SDK types. |
| `Enactive.Providers` | `OpenAiCompatibleProvider`, `OllamaNativeProvider` (native `/api/chat` so per-run `num_ctx` works), `AnthropicProvider` (reasoner) behind `ChatProviderFactory`; `LoggingChatProvider` + `WireTap`. |
| `Enactive.Tools` | `write_file`, `edit_file`, `read_file` (windowed), `search_files`, `list_dir`, `create_directory`, `move_file`, `run_command` (cmd.exe/sh), `run_powershell` (`-EncodedCommand`, no quoting), `git`, `docker`; `LoggingToolRegistry`. Every path goes through `WorkspacePaths` (inside the workspace or refused). A command may declare `expectedExitCodes` — the exit codes that ARE its answer (a test runner reporting failures) — before it runs; the declaration is visible in the evidence and judged there. A lookup that finds nothing (`read_file` on a missing path or past the end) returns `ToolResults.NotFound`, an answer rather than a failure. |
| `Enactive.Workspace` | Run, project-memory and inbox stores, each SQLite/MySQL/JSON behind `RunStoreFactory` / `MemoryStoreFactory` / `InboxStoreFactory`; artifact stores (disk + staging), `EnvironmentProbe`, `ProjectMemory`, `LogHub`/`FileLogSink`. |
| `Enactive.Agents` | `Orchestrator` (DAG execution, role tool-filtering, permission gating, open-failure and stall guards, optional reasoner plan+review, success criteria), `Planner` (dependency graphs), `DagScheduler`, `Reviewer` (judges the step's own evidence journal, not the transcript), `SuccessEvaluator`, `LogAnalyst` (a model reads an exported log; the log is data, never instructions), `DefaultWorkers`, `BackgroundRunner`, the unattended decision handler. |
| `Enactive.Secrets` | DPAPI protection for API keys; a key that cannot be protected is not written down. |
| `Enactive.App.Console` | Console host; sub-commands `timeline`, `inbox`; `--template <id> --workspace <path>` runs a saved task unattended with an exit code a scheduler can read. |
| `Enactive.App.Ui` | Avalonia desktop UI: `.axaml` views over view models (`ViewModels/`), with a small hand-written MVVM base in `Mvvm/`, app-wide styling in `Styles/Controls.axaml` and the palette in `Brand.cs`. |

## UI

The desktop UI is XAML over view models, so layout and styling are edited in `.axaml` without
touching C#.

| Piece | Where |
|---|---|
| Views | `App.axaml`, `MainWindow.axaml`, `SettingsWindow.axaml`, `LogWindow.axaml`, `InboxWindow.axaml`, `ProviderEditWindow.axaml`, `WorkerEditWindow.axaml`, `StepCardView.axaml` |
| View models | `ViewModels/` — one per view, plus the small item models the lists are built from (step entries, artifacts, staged changes, diff lines, decision options) |
| MVVM base | `Mvvm/` — `ObservableObject` and `RelayCommand`/`AsyncRelayCommand`. No toolkit dependency |
| Styling | `Styles/Controls.axaml` — the shared classes (`.label`, `.hint`, `.form`, `.actions`, `.primary`, `.multiline`, `.panelHeader`, `.disclosure`) |
| Colour | `Brand.cs` is the single definition, mirroring `brand/brand.css`. `Brand.PublishTo` puts every token into the application's resources, so XAML uses `{DynamicResource Brand.Accent}` and **no `.axaml` file contains a hex value** |

Two build settings matter:

- `AvaloniaUseCompiledBindingsByDefault` is on, so every view declares `x:DataType` and a mistyped
  binding path is a **build error**, not an empty control at runtime.
- `AvaloniaNameGeneratorAttachDevTools` is off: the generator would otherwise emit
  `this.AttachDevTools()` into `InitializeComponent`, which needs the `Avalonia.Diagnostics`
  package this project does not reference.

What stays in code-behind is only what needs the control or the window itself: focus and the caret
in the command bar, the key tunnel for `Ctrl+K` / `Enter` / `Ctrl+Enter`, the window icon and saved
bounds, following the tail of the log list, and opening child windows. Saved window bounds are
applied **after** `InitializeComponent`, or the XAML's own `Width`/`Height` overwrite them.

## Features

- **Tasks with a real DAG plan** — the planner emits step dependencies; `DagScheduler` runs steps by
  readiness (not a fixed line), cascade-skips dependents on failure, detects cycles.
- **Parallel branches** — `MaxParallelSteps` (Settings → General, default **1**) is how many independent
  steps may run at once. At 1 nothing changes: one step at a time on one shared conversation. Above 1,
  each concurrent step gets its **own forked conversation** — two steps cannot append to one message
  list — seeded with the base prompt plus a one-line digest of what earlier steps concluded, so a branch
  knows its siblings' conclusions without replaying their tool transcripts. Approval prompts are
  serialised (one card at a time) and the shared artifact list is locked. Every event carries its step
  number in `PayloadJson` (`{"step":3}`), which is how the UI keeps concurrent steps on their own cards.
  Worth raising mainly when steps route to different providers — two steps on one Ollama still queue on
  the GPU.
- **Task templates** — a saved task is data: goal with `{placeholders}`, typed parameters, a permission
  CEILING (it can deny, never grant), success criteria, limits. Built-ins ship read-only; user templates live in
  `%APPDATA%/Enactive/templates/`, repository ones in `<workspace>/.enactive/templates/`. A template is resolved
  against a workspace into one frozen specification the run records verbatim; only DECLARED placeholders are
  replaced, in the goal and in each criterion's command. The library generates a launch form from the parameters.
  See `TASK_TEMPLATES_PLAN.md`.
- **"Done" is not the model's opinion** — three guards decide a step, in this order: a tool call that failed and
  was never made good ends the step Incomplete (a lookup that found nothing is an answer, not a failure, unless the
  step did nothing else); three turns that only repeat calls already made end it as stuck — but a successful write
  advances a GENERATION, so re-reading a file and re-running a build after an edit is progress, not repetition, and
  a repair loop is never stopped for repairing; and a reviewer, when bound, judges the step's **evidence journal**
  (every call, its arguments, its outcome, its output — recorded as it happens, so shortening the prompt cannot
  shorten the evidence), told which parts it must not hold against the agent. Success criteria then run once at the
  end. A failing check beats the model saying it is done.
- **Permissions + decisions** — autonomy slider (Observe/Suggest/Execute/Autonomous); run_command/run_powershell ask before running. The approval card can **remember** an allow for the session or the workspace (`.enactive/permissions.json`).
- **Team of models** — configure any number of providers (Ollama, Anthropic, OpenAI-compatible) and an editable team of workers, each with its own model, and bind a model per phase: **Plan**, **Review**, and per-step **Execute** auto-routing (the planner rates each step trivial/normal/complex → light/worker/heavy model). A reasoner plans and reviews each step against the **real tool transcript**; on FAIL the step is retried with feedback. Robustness: local reasoning (`<think>`) off by default, Anthropic `temperature` auto-dropped and `max_tokens` auto-sized to the model's cap, a token-limit truncation guard, and optional read-back verification of writes. See `MODELS.md`.
- **Workers as roles** — Developer / Reviewer (read-only) / Ops / Writer, each with its own tool allowlist and permission level; pick one per run.
- **Environment awareness** — read-only discovery of host/OS, git (branch/remote/dirty), Docker, WSL, services, and a live snapshot; fed into the prompt and shown in the UI.
- **Timeline as project memory** — decisions and artifacts across all runs, decisions folded into a persistent memory store.
- **AI Inbox + background tasks** — run headless; results/decisions land in the Inbox.
- **Global log** — every prompt, response, tool call and event, with a raw-wire option; live window + daily file.
  **Log analysis**: the log window sends an exported run to a model for a diagnosis; the log is fenced as DATA,
  an instruction found inside it is never followed, and the analysis's own prompts are logged without their bodies
  so it cannot recurse on itself. See `LOGGING.md`.
- **Change staging** — optional stage → diff → Apply/Reject before writing. Staging is a TEXT diff:
  it cannot express a deletion or carry binary content, and an operation needing either is refused
  before it does the half of itself that works (`IArtifactStore.CanRemove`).
- **Undo and revert that refuse rather than guess** — every write is journalled with the bytes it
  displaced, a sequence number, the scope that made it and one canonical key per file. A revert
  undoes its own scope's writes; a file another scope has written since, or a file the user has
  edited since, is REPORTED with the reason and left alone. `Docs/FIX_PLAN.md` §9u is what each of
  those clauses cost to learn.
- **Secrets** — the Anthropic API key is DPAPI-encrypted in `settings.json`.

## Build & run

```bash
dotnet build

# console: run one command, or view history
dotnet run --project src/Enactive.App.Console -- "Create a Python script that lists files" "C:\path\to\ws"
dotnet run --project src/Enactive.App.Console -- timeline "C:\path\to\ws"
dotnet run --project src/Enactive.App.Console -- inbox "C:\path\to\ws"

# desktop UI
dotnet run --project src/Enactive.App.Ui
```

Prerequisites: **.NET 10 SDK**, **Ollama** with a tool-capable model (`ollama pull qwen2.5-coder`), and
optionally an **Anthropic API key** for multi-agent mode. On Windows, prefer `run_powershell` for
WMI/CIM/Get-PSDrive/pipes.

One operational rule learned the hard way (`FIX_PLAN.md` §9p): bind **Review** to the strongest model you have,
not the same local model that executes. A weak reviewer rejects true reports over evidence it misreads, and no
amount of prompt text fixes that.

```bash
# run a saved task unattended (exit code: 0 completed, non-zero otherwise)
dotnet run --project src/Enactive.App.Console -- --template improve-tests --workspace "C:\path\to\ws" --param test_command="dotnet test"
```

### Configuration

Env: `ENACTIVE_MODEL`, `ENACTIVE_OLLAMA_URL`, `ENACTIVE_STORE` (sqlite|mysql|json), `ENACTIVE_MYSQL`,
`ENACTIVE_LOG_LEVEL`, `ENACTIVE_WORKSPACE` (UI). The UI persists endpoint/model/num_ctx/global-instructions/
the provider list / worker team / phase bindings / toggles to `%APPDATA%/Enactive/settings.json` (API keys encrypted). Per-run data lives in
`<workspace>/.enactive/` (enactive.db, memory.json, inbox.json, permissions.json).

`ENACTIVE_STORE` picks the run store, the project-memory store **and** the inbox together, so
everything a workspace persists lands in the same place. At the default (`sqlite`) all three are
tables in `enactive.db`; the first time a workspace opens on SQLite its existing `memory.json` and
`inbox.json` are imported once, so switching stores does not empty its timeline or its inbox. The
JSON files are left where they are, and `ENACTIVE_STORE=json` still reads them. Moving existing data
into a server database is a migration you do yourself - MySQL does not import.

On MySQL the memory and inbox tables are scoped by workspace id, because one database serves every
workspace pointed at it. For the inbox that is not only about reading: an unscoped "mark all read"
would clear other projects' inboxes as a side effect of opening this one.

That scoping is why a workspace's id is **derived from its folder path** (`WorkspaceInfo.IdFor`,
SHA-256 over the normalised path, case folded where the filesystem folds it) rather than generated
per session. A random id is written into every row just the same, and then matches nothing the next
time the folder is opened.

### Naming / migration (AIClient -> Enactive)

The project was renamed from **AIClient** to **Enactive** (domain `enactive.dev`). The rename is total —
it covers namespaces, assemblies **and every on-disk name**, so a build of the new code does not see the
old installation's data:

| Was | Is now |
|---|---|
| `AIClient.sln`, `src/AIClient.*` | `Enactive.sln`, `src/Enactive.*` |
| namespaces `AIClient.*` | namespaces `Enactive.*` |
| assemblies `aiclient`, `aiclient-ui` | `enactive`, `enactive-ui` |
| `AICLIENT_MODEL`, `AICLIENT_OLLAMA_URL`, `AICLIENT_STORE`, `AICLIENT_MYSQL`, `AICLIENT_LOG_LEVEL`, `AICLIENT_WORKSPACE` | the same names with the `ENACTIVE_` prefix |
| `%APPDATA%/AIClient/settings.json`, `workspaces.txt`, `logs/aiclient-*.log` | `%APPDATA%/Enactive/...`, `logs/enactive-*.log` |
| `<workspace>/.aiclient/` (`aiclient.db`, memory.json, inbox.json, permissions.json) | `<workspace>/.enactive/` (`enactive.db`, ...) |
| DPAPI entropy `AIClient.settings.v1` | `Enactive.settings.v1` |

There is **no automatic migration**. After the rename:

1. Settings start from defaults — copy `%APPDATA%/AIClient/settings.json` to `%APPDATA%/Enactive/settings.json`
   to keep providers, the worker team and phase bindings.
2. **API keys must be re-entered.** They are DPAPI-encrypted with the entropy above; the new entropy cannot
   decrypt the old blobs, so a copied `settings.json` will fail to unprotect them. Clear the key fields and
   paste the keys again in Settings -> Providers.
3. Per-workspace history (runs, project memory, inbox, remembered approvals) lives in the old `.aiclient`
   folder; rename it to `.enactive` inside each workspace to keep it, or leave it and start clean.
