# Operations and Troubleshooting

[Wiki home](README.md)

## Run history and project memory

The desktop records run requests, events, decisions, artifacts, outcomes, usage, and available settings/specification snapshots. Open a historical run to inspect its execution cards and timeline. Retry and Run again create additional attempts; see [Running tasks](Running-Tasks.md#retry-versus-run-again).

Project memory stores durable entries such as decisions and contributes to the project timeline. It is not a full source index or a guarantee that every entry is inserted into every future model prompt.

Historical artifact actions operate on current files. Opening a path shows what is there now; a Delete action on an old artifact is not equivalent to reverting the exact historical write. Inspect the current content and action label.

## Storage layout

### User-level data

| Path under `%APPDATA%\Enactive` | Contents |
| --- | --- |
| `settings.json` | Desktop provider/team/phase configuration and other settings |
| `workspaces.json` | Workspace registry and remembered run preferences |
| `permissions.json` | Desktop workspace approvals keyed by workspace ID |
| `templates\` | Global template files |
| `logs\` | Daily application logs |

### Workspace-level data

| Path under `<workspace>\.enactive` | Contents |
| --- | --- |
| `templates\` | Workspace template files |
| `enactive.db` | Default SQLite run, memory, and Inbox storage |
| `runs\` | Run files when using the JSON backend |
| `memory.json` | JSON project-memory backend / possible legacy source |
| `inbox.json` | JSON Inbox backend / possible legacy source |

Additional artifact-store state supports tracked write recovery. Treat `.enactive` as application state rather than a folder the worker should edit. Built-in path handling reserves it.

Version workspace templates intentionally. Do not assume the whole `.enactive` directory should be committed: it can contain local history, tool outputs, and machine-specific state.

### Storage backend selection

`ENACTIVE_STORE` chooses SQLite (default), JSON, or MySQL for runs, memory, and Inbox together. MySQL requires `ENACTIVE_MYSQL` with a connection string.

SQLite imports existing legacy memory/Inbox JSON once and leaves those source files in place. This is not continuous synchronization between backends. MySQL does not automatically import local history; plan a migration explicitly.

Workspace identity is written into `<workspace>/.enactive/workspace.json` the first time a run takes the folder up, seeded with the id the workspace already had. In MySQL, records are scoped by that identity. Because the marker travels with the folder, **renaming or moving a workspace keeps its history**; before this it appeared as a different workspace and its records silently stopped matching.

Notes on that marker:

- It is created by a **run**, not by browsing. Opening a folder's history does not write anything into it.
- If it is missing or damaged, the workspace falls back to the path-derived id — the old behaviour — rather than becoming a new workspace with no history.
- A folder that cannot be written to still opens; the marker is best-effort.
- **Copying** a workspace copies its identity, so the copy and the original share a history. Renaming and copying are indistinguishable from inside one folder.
- `.enactive/` is not tracked by git, so two clones of one repository remain two workspaces.
- Remembered **"Allow (workspace)" approvals are deliberately not keyed by this id** — they stay keyed to the path. The marker sits in the folder the agent works in and can arrive inside a cloned repository, so it must never be able to carry permissions. Renaming a folder therefore asks for those approvals again, once per tool.

## Logs and diagnosis

The application has a global log with provider activity, tool calls, and execution events, mirrored to daily files. The desktop's Log window lets you inspect and export information; the per-run Log view focuses on the selected run.

`ENACTIVE_LOG_LEVEL` supports Trace, Debug, Info, Warn, and Error. Trace can capture raw model HTTP request/response bodies. Log exports can contain source code, command output, and task content; review an export before sharing it.

Log analysis can send an exported run to a model for diagnosis. The analyst treats the exported log as data rather than instructions. The analysis still consumes model work and may transmit log content to the configured provider.

For a useful issue report, include:

- Application/build version from About.
- Host OS and relevant provider kind/model ID.
- Workspace task/template ID and supplied parameters, with secrets removed.
- Expected outcome and actual terminal outcome.
- Relevant route, tool, error, and criterion events.
- Whether staging, background mode, or console mode was used.

Avoid using an old fixed test-count claim as a release check. Run the current test project and record its actual result.

## MCP external tools

### Configure a server

Open **Settings → AI → MCP → Add**. Configure the server and use **Test connection**, then accept the editor and Save the main Settings window.

| Field | Meaning |
| --- | --- |
| ID | Unique lowercase identifier, 1–24 characters, starting with a letter; digits/hyphens supported |
| Enabled | Connect this server for a new desktop run |
| Transport | Stdio local process or Http endpoint |
| Command / Arguments | Executable plus a JSON array of arguments for Stdio |
| Working directory | Process directory; blank uses the run workspace, while editor Test uses the app's current directory |
| Environment | JSON dictionary for the child process |
| URL / Headers | Endpoint and JSON header dictionary for Http |
| Timeout | 1–600 seconds for connection/discovery and individual calls |
| Ask before every tool call | Enabled by default; forces individual approval |

Test connection initializes the server and discovers tools; it does not invoke the discovered tools. Starting the configured program can itself have effects, for example a package launcher downloading and running a package.

### Grant worker access

In **AI → Team**, grant an appropriate pattern:

```text
mcp__*
mcp__example__*
```

Or use an exact tool name from discovery. Discovered names include the server ID and a stable suffix to distinguish tools. The worker must also satisfy normal role/policy checks; enabling a server does not automatically grant every worker access.

### Runtime behavior

Each desktop run gets its own server connections. Any enabled server that fails to connect/discover can stop launch. Disable unused broken configurations rather than waiting for a run to discover the same failure again.

Connections and child processes are released when the run ends or is cancelled. A transport error does not trigger automatic replay of a remote tool call: the external action may already have happened.

MCP environment values and headers are saved through encrypted secret storage. Arguments and URLs are not secret fields. If decryption is unavailable, the configuration requires attention rather than silently discarding the original protected data.

External tools operate with their process/account permissions. Enactive's workspace guard, staging, and rejected-step revert do not constrain or undo their external effects. Configure boundaries in the server itself when needed. The console does not connect these desktop MCP configurations.

## Filesystem and recovery boundaries

Built-in path-based file tools reject absolute/escaping paths, inspect links that can lead outside the workspace, and protect reserved application-state paths. These checks are not a general sandbox for shell or external servers.

Tracked writes use canonical file identity and scoped sequence information. Revert can refuse when a later scope wrote the same file, the user edited it, or the operation cannot be represented safely. Read the refusal instead of assuming that Failed means all files were restored.

Use version control or an independent backup for recovery beyond the artifact journal. A tool-driven command can have effects that cannot be expressed as a text-file undo.

## Backup and moving installations

For a restorable desktop setup, preserve both user-level settings/templates and workspace state. Stop active work and use a consistent database backup approach; copying a live SQLite database without considering its journal/WAL is not a reliable backup procedure.

After restoring to another account or machine, verify provider and MCP credentials. DPAPI-protected material may need to be entered again. Recheck workspace paths, local model installation, and project dependencies.

The historical AIClient → Enactive rename changed names, environment-variable prefixes, state folders, and secret-protection entropy. There is no complete automatic migration from that product name. Preserve original data before making a manual migration, and re-enter keys rather than assuming copied encrypted values will decrypt. See the historical [migration notes](../Docs/README.md#naming--migration-aiclient---enactive).

## Troubleshooting reference

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| Desktop says “No such folder” | Workspace path is absent or wrong | Select/create the intended folder before running |
| Console worked in an unexpected empty folder | Console created a misspelled workspace | Inspect the printed absolute workspace path |
| Provider cannot be reached | Service down, wrong URL, authentication, or network issue | Provider kind, API prefix, service availability, returned error |
| Model appears in a dropdown but calls fail | Catalog entry is not a compatibility test | Exact installed/available ID; endpoint/field/tool support |
| OpenAI-compatible request returns 400 | Unsupported request field or wrong API surface | Actual Chat Completions payload, especially temperature/token fields |
| Model describes JSON instead of doing work | Missing/unreliable structured tool calls | Use a tool-capable model; leave implicit calls off by default |
| Local model is very slow | Memory pressure, context size, queued requests | Reduce context/concurrency and inspect local provider utilization |
| Review does not run | Review binding is blank | AI → Phases; `ReviewRequired` alone does not enable it |
| Review rejects apparently correct work repeatedly | Reviewer misreads evidence or task/check mismatch | Read journal and feedback; qualify a stronger reviewer |
| Content review toggle has no effect | No reviewer, or the step uses execution-evidence review | Review binding and the actual step's operations |
| Template vanished | Malformed/unreadable JSON or unsafe ID | File syntax, ID, scope folder, and reload |
| Global edit has no effect | Workspace definition shadows it | Origin metadata and matching IDs |
| Workspace edit seems unchanged | Editor saved to Global | Explicit Scope selection; remaining local override |
| Different commands run during final checks | Built-in criteria are literal .NET commands | Edit criterion Command fields as well as goal parameters |
| Template cannot run in console | Missing required inputs | Supply repeated `--param "id=value"` arguments |
| Scheduled build/test task is Incomplete | Console AskBefore plus unattended denial | Current console limitation; use interactive desktop or extend the host |
| Background task refuses an action | No approver in background handler | Open Inbox and re-run interactively |
| Background launch says Not started | Stage changes is enabled | Use foreground staging or turn staging off deliberately |
| Build passes while a proposed edit is wrong | Build saw disk, not unapplied staged content | Understand staging before trusting the check |
| Undo/revert leaves a file | Later write/user edit or unsupported effect | Read the conflict/revert event; inspect current content |
| Changing desktop settings does not change CLI | Separate composition roots | Console environment configuration |
| A custom WorkerId uses another role | Unknown ID falls back to default | Team IDs and console's built-in-only team |
| MCP failure prevents all work | An enabled server cannot initialize | Test, fix, or disable that server |
| App closes and background work stops | Process exited rather than hiding | Close-to-tray and explicit Quit behavior |

## Remote server preview

`server/` is an independent ASP.NET Core/.NET 10 gateway with a plain HTML/CSS/JavaScript panel. It is not in `Enactive.sln` and is not required to run desktop or console tasks.

### Implemented preview features

- Single-owner login using a configured private key, cookie sessions, and CSRF protection.
- Computer registration with a separate device token and revocation.
- SignalR host connections and host-published workspaces.
- Task drafts, queued run commands, retry/cancel requests, and command acknowledgements.
- Approval requests/responses, stored events, outcomes, and an internal Inbox.
- Task search/filtering, computer/history views, and responsive light/dark UI.

The browser polls state approximately every three seconds; host command delivery uses periodic Sync. This is a trusted gateway that can see task text and results, not an end-to-end encrypted execution channel.

### Local start

From `server/`:

```powershell
./scripts/dev.ps1
```

The script starts the local preview at `http://127.0.0.1:5187` and creates a local owner key in `data/dev-owner-key.txt`. Use the generated key to log in. The data directory is excluded from Git.

Manual configuration uses `ENACTIVE_OWNER_KEY` (at least 24 characters), `ENACTIVE_DATA` for the data directory, standard ASP.NET environment settings, and Kestrel listen configuration. Development HTTP is for local use; production exposure requires its own verified deployment setup.

### Validation commands

From `server/`, with .NET 10 and Node.js 22+:

```powershell
dotnet build Enactive.Server.csproj
node --test tests/gateway.test.mjs
```

The test host client is a protocol fixture. It does not execute shell commands or inspect real workspace files. A production bridge to Enactive's local engine, durable host-side queues/recovery, pairing, external notifications, retention, and production deployment remain separate work. Do not interpret the browser's task UI as proof that those integrations exist.

See [server README](../server/README.md) and [protocol](../server/PROTOCOL.md) for the maintained preview contract.

## Maintaining this wiki

Update the relevant page whenever a behavior changes in host composition, template resolution, permission matching, settings persistence, or model adapters. In particular, recheck the documented limitations before removing them: many of these behaviors cannot be inferred from UI labels or schema fields alone.

For documentation-only changes, validate relative links, headings/anchors, and executable example syntax. Use the engine's template validator/resolver for changed template examples. A full model-backed run is unnecessary unless the documentation claims live provider compatibility.

## Implementation references

- [Run store selection](../src/Enactive.Workspace/RunStoreFactory.cs)
- [Memory store selection](../src/Enactive.Workspace/MemoryStoreFactory.cs)
- [Inbox store selection](../src/Enactive.Workspace/InboxStoreFactory.cs)
- [Logging](../src/Enactive.Workspace/LogHub.cs)
- [MCP run connections](../src/Enactive.Tools/Mcp/McpRunTools.cs)
- [Artifact recovery](../src/Enactive.Workspace/DiskArtifactStore.cs)
- [Remote server](../server/Program.cs)
