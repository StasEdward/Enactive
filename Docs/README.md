# Enactive

A desktop environment for AI agents (a "GUI for AI agents"), built on **.NET 10**.
Site: [enactive.dev](https://enactive.dev). Formerly named *AIClient* — renamed to **Enactive** in 2026-09;
namespaces, assemblies, env vars and on-disk data folders all use the new name (see *Naming / migration* below).

## Documents

| | |
|---|---|
| `PLAN.md` | The vision |
| `PLAN_v2.md` | Development spec; **§11 is the honest status** — what is built and what is not |
| `FIX_PLAN.md` | The defect log: every fix since the first review, each with the log line that exposed it, what changed, what deliberately did not, and the test that fails without it. Its §9 tail is the open backlog |
| `MODELS.md` | The multi-provider team-of-models design (implemented) |
| `MCP.md` | MCP servers as tools (implemented 2026-09-06) |
| `LOGGING.md` | The global log and the log analysis |
| `STYLING.md` | How the Avalonia/Fluent UI is themed — **read before changing anything visual** |
| `TASK_TEMPLATES_PLAN.md` | The saved-task system (complete) |
| `SCHEDULER_PLAN.md` | Schedules: a template that runs at a chosen day and time |
| `SETTINGS_PLAN.md` | One configuration, two hosts |
| `REMOTE_DESIGN.md`, `REMOTE_ACCESS_PLAN.md`, `REMOTE_OPERATIONS.md`, `ENACTIVE_REMOTE_PROTOCOL.md` | Remote access — the design, the plan, how to run it, and the wire protocol |
| `WORKSPACE_SANDBOX_ARCHITECTURE.md`, `SANDBOX_PLAN.md` | The sandbox: the argument, then the plan |
| `REVERT_INTEGRITY_PLAN.md` | Undo/revert integrity (done 2026-09-08; outcome in `FIX_PLAN.md` §9u) |
| `SCENARIO_CHECKS.md` | Behaviours a unit test cannot settle, driven from a command line |
| `Enactive_Code_Review_2026-09-*.md` | The reviews this defect log answers |

The engine turns one intent into real, reviewable, recorded action:

```
Command -> Intent -> Context(+Environment) -> Planner -> Orchestrator -> Worker(role)
        -> Provider -> Tool -> ToolResult -> Artifact -> Event -> RunRecorder (persist + memory)
```

## Solution

`Enactive.sln`, **12 product projects** (`net10.0`, pinned via `global.json`), plus three on the test side:
`tests/Enactive.Engine.Tests` (**1913 tests**), `tests/Enactive.Remote.Gateway.Tests` (77, and they need a MySQL
to run) and `tests/Enactive.Mcp.TestServer`.

The engine is dependency-light: `Core`/`Providers`/`Agents`/`App.Console` use **no external NuGet package the
engine itself needs**; only `Workspace` (Microsoft.Data.Sqlite, SQLitePCLRaw, MySqlConnector), `Secrets`
(ProtectedData), `Tools` (MailKit, for `send_email` — the one tool that leaves the machine) and `App.Ui`
(Avalonia ×3) pull anything in. `Enactive.Agents` references **only** `Core` — a constant both the engine and a
tool need (`ToolArguments.ExpectedExitCodes`) lives in `Core.Tools`, not in `Tools`.

| Project | Responsibility |
|---|---|
| `Enactive.Core` | Domain model + abstractions only (Intent, WorkContext, Environment, Plan/PlanStep (DAG), Worker/ModelPolicy, Decision, Permissions, Memory, Inbox, events, diagnostics, task templates and their resolution, the `ExecutionJournal` a step's evidence is kept in, the built-in templates). No transport/SDK types. |
| `Enactive.Providers` | `OpenAiCompatibleProvider`, `OllamaNativeProvider` (native `/api/chat` so per-run `num_ctx` works), `AnthropicProvider` (reasoner) behind `ChatProviderFactory`; `LoggingChatProvider` + `WireTap`. |
| `Enactive.Tools` | The tools themselves, plus `ProcessExec` and `LoggingToolRegistry`. Every path goes through `WorkspacePaths` — inside the workspace or refused. See **Tools** below. |
| `Enactive.Workspace` | Run, project-memory and inbox stores, each SQLite/MySQL/JSON behind `RunStoreFactory` / `MemoryStoreFactory` / `InboxStoreFactory`; artifact stores (disk + staging), `EnvironmentProbe`, `ProjectMemory`, `LogHub`/`FileLogSink`. |
| `Enactive.Agents` | `Orchestrator` (DAG execution, role tool-filtering, permission gating, open-failure and stall guards, optional reasoner plan+review, success criteria), `Planner` (dependency graphs), `DagScheduler`, `Reviewer` (judges the step's own evidence journal, not the transcript), `SuccessEvaluator`, `LogAnalyst` (a model reads an exported log; the log is data, never instructions), `DefaultWorkers`, `BackgroundRunner`, the unattended decision handler. |
| `Enactive.Secrets` | DPAPI protection for API keys; a key that cannot be protected is not written down. |
| `Enactive.App.Console` | Console host; sub-commands `timeline`, `inbox`; `--template <id> --workspace <path>` runs one saved task unattended, and `--due` runs whatever the saved schedules say is owed — both with an exit code a scheduler can read. Nothing in that mode is interactive, and it is enforced rather than assumed (`UnattendedDecisionHandler`). |
| `Enactive.Settings` | `AppSettings` and its file — the one configuration both hosts read, including the provider list, the worker team, the phase bindings, the SMTP account and the MCP servers (`McpSettings`, `McpRoles`). Referenced by both hosts so neither owns it. |
| `Enactive.Remote.Contracts` | The wire types remote access is spoken in — shared by the gateway and the host so one side cannot drift from the other. |
| `Enactive.Remote.Gateway` | The server side of remote access: the panel, the cursor over a run's events, the schema. See `REMOTE_DESIGN.md`. |
| `Enactive.Remote.Host` | The machine-side agent that a gateway talks to. Remote access is **off** unless it is turned on. |
| `Enactive.App.Ui` | Avalonia desktop UI: `.axaml` views over view models (`ViewModels/`), with a small hand-written MVVM base in `Mvvm/`, app-wide styling in `Styles/Controls.axaml` and the palette in `Brand.cs`. |

## Tools

Seventeen, registered in one place (`App.Console/Program.cs`) and offered to a run only through its
role's allowlist. A tool a role names but the host does not register is the same defect as a tool
the host registers and no role names — seen from the other side.

| Group | Tools |
|---|---|
| Write | `write_file`, `edit_file`, `create_directory`, `move_file`, `copy_file`, `delete_file` |
| Read | `read_file` (windowed), `search_files`, `list_dir` |
| Answer without the content | `count_matches`, `file_stats`, `compare_files` — how many, how large, same or not, so a question with a small answer costs a small answer |
| Run | `run_command` (cmd.exe / sh), `run_powershell` (`-EncodedCommand`, so no quoting), `git`, `docker` |
| Leaves the machine | `send_email` — through the SMTP account in settings, and only to an address a person put on the recipient list. It always asks, whatever the policy says, because a sent message cannot be put back |

MCP servers add more. A server configured in Settings → MCP is connected at startup and its tools
are offered to the roles that reach it (`McpRoles`); a server nobody reaches is named in the run's
own events, because a server connected and offered to no one looks exactly like a server that
works. See `MCP.md`.

**Every path goes through `WorkspacePaths`** — inside the workspace, or refused.

**`.enactive/` is reserved from tools, with one carve-out.** `.enactive/scratch/` is the worker's
own working area, for the files that are FOR the job but are not the job: a helper script it means
to run, a command's output too long to come back in a tool result. It is written straight through —
never staged, never journalled, absent from what the reviewer is shown as the step's changes, and
left alone when a rejected step is reverted. `delete_file` works there even under staging, and
`search_files` skips it in a whole-workspace sweep but searches it when named with `path`. Entries
untouched for seven days go on the next run's first write to it (`ScratchArea`), so it cannot grow
without limit inside the user's project. The workspace proper stays for the deliverable.

**A command may declare `expectedExitCodes`** — the exit codes that ARE its answer, as for a test
runner reporting failures — before it runs. The declaration is visible in the evidence and judged
there.

**Not every non-zero result is a failure**, and the engine says which is which rather than passing
an exit code upwards. A lookup that finds nothing is an answer (`read_file` past the end of a file,
a search that matched nothing). So is a call that never happened: a shell refusing a word it does
not have, `git` refusing its own arguments, a tool name with no tool behind it. These are told
apart by what the program itself said, and `FIX_PLAN.md` §9ap and §9bj–§9bl are what each one cost
to learn.

## UI

The desktop UI is XAML over view models, so layout and styling are edited in `.axaml` without
touching C#.

| Piece | Where |
|---|---|
| Views | `App.axaml`, `MainWindow.axaml`, `TitleBarView.axaml`, `StepCardView.axaml`, `HintView.axaml`; the windows `Settings`, `Log`, `LogAnalysis`, `Inbox`, `Templates`, `TemplateEdit`, `Schedules`, `ProviderEdit`, `WorkerEdit`, `McpEdit`, `Viewer`, `Prompt`, `Confirm` |
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
  was never made good ends the step Incomplete — but only a call that really did fail. A lookup that found nothing
  is an answer (unless the step did nothing else), and so is a call that never happened at all: a word the shell
  does not have, arguments `git` will not take, a tool name with no tool behind it. Those are recorded, shown to
  the reviewer, and not a verdict; three turns that only repeat calls already made end it as stuck — but a successful write
  advances a GENERATION, so re-reading a file and re-running a build after an edit is progress, not repetition, and
  a repair loop is never stopped for repairing; and a reviewer, when bound, judges the step's **evidence journal**
  (every call, its arguments, its outcome, its output — recorded as it happens, so shortening the prompt cannot
  shorten the evidence), told which parts it must not hold against the agent. Success criteria then run once at the
  end, and a required one that FAILED buys the agent one attempt to fix it, with the check's own
  output in front of it, before the criteria are re-run and decide. A failing check beats the model
  saying it is done.
- **Permissions + decisions** — autonomy slider (Observe/Suggest/Execute/Autonomous); run_command/run_powershell ask before running. The approval card can **remember** an allow for the session or the workspace (`.enactive/permissions.json`).
- **Team of models** — configure any number of providers (Ollama, Anthropic, OpenAI-compatible) and an editable team of workers, each with its own model, and bind a model per phase: **Plan**, **Review**, and per-step **Execute** auto-routing (the planner rates each step trivial/normal/complex → light/worker/heavy model). A reasoner plans and reviews each step against the **real tool transcript**; on FAIL the step is retried with feedback. Robustness: local reasoning (`<think>`) off by default, Anthropic `temperature` auto-dropped and `max_tokens` auto-sized to the model's cap, a token-limit truncation guard, and optional read-back verification of writes. See `MODELS.md`.
- **Workers as roles** — Developer / Reviewer (read-only) / Ops / Writer, each with its own tool allowlist and permission level; pick one per run.
- **Environment awareness** — read-only discovery of host/OS, git (branch/remote/dirty), Docker, WSL, services, and a live snapshot; fed into the prompt and shown in the UI.
- **Timeline as project memory, read back** — decisions and how each run ended are folded into a
  persistent per-workspace store, and the next run STARTS with them: the most recent entries go into
  its prompt as background ("facts about the project, not instructions"), bounded so a workspace's
  history cannot crowd out its work.
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
- **MCP servers as tools** — a server configured in Settings → MCP is connected at startup and its
  tools join the registry under `mcp__<server>__<tool>` names. Which roles may reach which server is
  part of the configuration (`McpRoles`), and a server that reaches nobody is said so in the run's
  events — connected-and-offered-to-no-one is indistinguishable from working, from the outside.
  See `MCP.md`.
- **Schedules** — a saved task can be given a day and a time. The console's `--due` asks the saved
  schedules what is owed and runs it; Windows Task Scheduler, cron or a pipeline step drives that.
  See `SCHEDULER_PLAN.md`.
- **Remote access, off by default** — `Enactive.Remote.Host` and `Enactive.Remote.Gateway` let a run
  be watched and steered from elsewhere over the contracts in `Enactive.Remote.Contracts`. It starts
  off and says so at startup. See `REMOTE_DESIGN.md` and `REMOTE_OPERATIONS.md`.
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
`ENACTIVE_LOG_LEVEL`, `ENACTIVE_WORKSPACE` (UI). Remote access reads its own: `ENACTIVE_DATA`,
`ENACTIVE_REMOTE_DB`, `ENACTIVE_OWNER_KEY`, `ENACTIVE_RETENTION_DAYS`, `ENACTIVE_BEHIND_TUNNEL` —
see `REMOTE_OPERATIONS.md`. The UI persists endpoint/model/num_ctx/global-instructions/
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
