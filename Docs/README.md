# AIClient

A desktop environment for AI agents (a "GUI for AI agents"), built on **.NET 10**.
See `PLAN.md` (vision) and `PLAN_v2.md` (development spec). `LOGGING.md` documents the global log.

The engine turns one intent into real, reviewable, recorded action:

```
Command -> Intent -> Context(+Environment) -> Planner -> Orchestrator -> Worker(role)
        -> Provider -> Tool -> ToolResult -> Artifact -> Event -> RunRecorder (persist + memory)
```

## Solution

`AIClient.sln`, **7 projects** (`net10.0`, pinned via `global.json`). The engine is dependency-light:
`Core`/`Providers`/`Tools`/`Agents`/`App.Console` use **zero external NuGet packages**; only `Workspace`
(Microsoft.Data.Sqlite, SQLitePCLRaw, MySqlConnector) and `App.Ui` (Avalonia ×3, ProtectedData) pull anything in.

| Project | Responsibility |
|---|---|
| `AIClient.Core` | Domain model + abstractions only (Intent, WorkContext, Environment, Plan/PlanStep (DAG), Worker/ModelPolicy, Decision, Permissions, Memory, Inbox, events, diagnostics). No transport/SDK types. |
| `AIClient.Providers` | `OpenAiCompatibleProvider`, `OllamaNativeProvider` (native `/api/chat` so per-run `num_ctx` works), `AnthropicProvider` (reasoner) behind `ChatProviderFactory`; `LoggingChatProvider` + `WireTap`. |
| `AIClient.Tools` | `write_file`, `read_file`, `list_dir`, `run_command` (cmd.exe/sh), `run_powershell` (`-EncodedCommand`, no quoting); `LoggingToolRegistry`. |
| `AIClient.Workspace` | Run stores (SQLite/MySQL/JSON via `RunStoreFactory`), artifact stores (disk + staging), `JsonMemoryStore`, `JsonInboxStore`, `EnvironmentProbe`, `ProjectMemory`, `LogHub`/`FileLogSink`. |
| `AIClient.Agents` | `Orchestrator` (DAG execution, role tool-filtering, permission gating, optional reasoner plan+review), `Planner` (dependency graphs), `DagScheduler`, `Reviewer` (evidence-aware), `DefaultWorkers`, `BackgroundRunner`. |
| `AIClient.App.Console` | Console host; sub-commands `timeline`, `inbox`. |
| `AIClient.App.Ui` | Avalonia desktop UI (code-only). |

## Features

- **Tasks with a real DAG plan** — the planner emits step dependencies; `DagScheduler` runs steps by readiness (not a fixed line), cascade-skips dependents on failure, detects cycles.
- **Permissions + decisions** — autonomy slider (Observe/Suggest/Execute/Autonomous); run_command/run_powershell ask before running. The approval card can **remember** an allow for the session or the workspace (`.aiclient/permissions.json`).
- **Multi-agent** (optional) — a reasoner (Anthropic) plans and reviews each step against the **real tool transcript** while the local Ollama model executes; on FAIL the step is retried with feedback.
- **Workers as roles** — Developer / Reviewer (read-only) / Ops / Writer, each with its own tool allowlist and permission level; pick one per run.
- **Environment awareness** — read-only discovery of host/OS, git (branch/remote/dirty), Docker, WSL, services, and a live snapshot; fed into the prompt and shown in the UI.
- **Timeline as project memory** — decisions and artifacts across all runs, decisions folded into a persistent memory store.
- **AI Inbox + background tasks** — run headless; results/decisions land in the Inbox.
- **Global log** — every prompt, response, tool call and event, with a raw-wire option; live window + daily file.
- **Change staging** — optional stage → diff → Apply/Reject before writing.
- **Secrets** — the Anthropic API key is DPAPI-encrypted in `settings.json`.

## Build & run

```bash
dotnet build

# console: run one command, or view history
dotnet run --project src/AIClient.App.Console -- "Create a Python script that lists files" "C:\path\to\ws"
dotnet run --project src/AIClient.App.Console -- timeline "C:\path\to\ws"
dotnet run --project src/AIClient.App.Console -- inbox "C:\path\to\ws"

# desktop UI
dotnet run --project src/AIClient.App.Ui
```

Prerequisites: **.NET 10 SDK**, **Ollama** with a tool-capable model (`ollama pull qwen2.5-coder`), and
optionally an **Anthropic API key** for multi-agent mode. On Windows, prefer `run_powershell` for
WMI/CIM/Get-PSDrive/pipes.

### Configuration

Env: `AICLIENT_MODEL`, `AICLIENT_OLLAMA_URL`, `AICLIENT_STORE` (sqlite|mysql|json), `AICLIENT_MYSQL`,
`AICLIENT_LOG_LEVEL`, `AICLIENT_WORKSPACE` (UI). The UI persists endpoint/model/num_ctx/global-instructions/
multi-agent to `%APPDATA%/AIClient/settings.json` (API key encrypted). Per-run data lives in
`<workspace>/.aiclient/` (aiclient.db, memory.json, inbox.json, permissions.json).
