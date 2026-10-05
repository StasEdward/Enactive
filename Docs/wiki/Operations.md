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
| Enable | Connect this server for each new desktop run; untick to stop using it without removing its configuration |
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

## Remote access

Starting tasks from a browser has its own chapter: [Remote access](Remote-Access.md), and what the
service can and cannot see is in [Remote security](Remote-Security.md). The full server runbook is
[REMOTE_OPERATIONS](../REMOTE_OPERATIONS.md); the parts below are the ones an operator reaches for
most.

Two things belong here because they are troubleshooting rather than setup.

**The panel shows an old build after a deploy.** It should not: the page's stylesheets and scripts
are addressed by a fingerprint of their own contents, so a new build is a new URL that no cache has
an answer for. If it happens anyway, the deploy has not run or has not succeeded — check
`journalctl -u enactive-deploy`, and remember that the deploy only installs a build that is green
in CI for the branch named in `/etc/enactive-remote/deploy.env`.

**A computer shows as Offline.** The host syncs every 15 seconds, is shown offline after 45 without
one, and backs off up to two minutes when the gateway is unreachable. Offline means the desktop
application is not running, remote access is off in its settings, the computer was revoked under
**Computers**, the account was disabled, or the computer is on a different gateway. Settings → Remote
access → **Test connection** says which; it connects and publishes this computer's workspaces, so a
success there is also what makes the workspaces selectable.

### Running the remote gateway

**Settings.** All are environment variables in `/etc/enactive-remote/gateway.env` (mode `0600`),
which `deploy/gateway.env.example` lays out with every one named. Every value is checked at start, and
a bad one stops the gateway with a sentence naming it.

| Variable | Default | One line |
| --- | --- | --- |
| `ENACTIVE_REMOTE_DB` | required | MySQL connection string for the protocol-2 database, `enactive_remote_v2` |
| `ENACTIVE_PUBLIC_ORIGIN` | — | The address people use, scheme and host only; the providers send people back to it |
| `ENACTIVE_GITHUB_CLIENT_ID`, `ENACTIVE_GITHUB_CLIENT_SECRET` | — | A GitHub OAuth app, callback `<origin>/auth/github/callback`; both or neither |
| `ENACTIVE_GOOGLE_CLIENT_ID`, `ENACTIVE_GOOGLE_CLIENT_SECRET` | — | A Google OAuth client, redirect `<origin>/auth/google/callback`; both or neither |
| `ENACTIVE_ADMISSION` | `list` | `list`: only approved identities may create an account; `open`: anyone who signs in |
| `ENACTIVE_LIMIT_HOSTS_PER_USER` | 5 | Computers per account |
| `ENACTIVE_LIMIT_DEVICES_PER_USER` | 10 | Devices per account |
| `ENACTIVE_LIMIT_ACTIVE_RUNS_PER_USER` | 3 | Runs in progress per account |
| `ENACTIVE_LIMIT_QUEUED_COMMANDS_PER_HOST` | 50 | Commands waiting for one computer; device removals and endorsements are counted apart, up to twice the devices limit |
| `ENACTIVE_LIMIT_TASKS_PER_DAY` | 200 | Tasks per account in the last 24 hours |
| `ENACTIVE_LIMIT_OPEN_INVITES_PER_USER` | 5 | Unused, unexpired invitations per account |
| `ENACTIVE_LIMIT_SEALED_BYTES_PER_USER` | 209715200 | Bytes of encrypted content per account (200 MiB) |
| `ENACTIVE_RETENTION_DAYS` | 30 | Days of history kept; older events, notices and ended runs are deleted hourly |
| `ENACTIVE_DEV_SIGNIN` | never set | A sign-in without a provider, for tests; refused outside Development |
| `ENACTIVE_GITHUB_BASE`, `ENACTIVE_GITHUB_API`, `ENACTIVE_GOOGLE_AUTHORITY` | never set | Move the providers to another host, for tests against a fake; outside Development they stop the start |

`ENACTIVE_DATA` (the Data Protection keys), `ENACTIVE_BEHIND_TUNNEL` and `ASPNETCORE_URLS` are set in
the systemd unit, and the backup's `ENACTIVE_BACKUP_*` settings on its cron line.

**Admitting people.** A command on the gateway's own binary, run on the server with the gateway's
environment ([how](../REMOTE_OPERATIONS.md#31-admitting-people-the-admission-cli)):

```text
admin admissions                 the identities waiting for approval
admin approve <provider>:<id>    let this identity create an account (e.g. github:12345)
admin refuse <provider>:<id>     turn this identity away
admin disable <userId>           stop an account: sign-ins, sessions and queued commands
admin enable <userId>            let a disabled account sign in again
admin sessions revoke <userId>   sign the account out everywhere
```

Every change is written to the audit trail as the operator. `refuse` does not stop an account that
already exists; `disable` does.

**The cutover from protocol 1** is done by hand onto a new database, and the deploy timer refuses to
do it: [REMOTE_OPERATIONS, the cutover](../REMOTE_OPERATIONS.md#8-the-cutover-from-protocol-1-cutover).

**Restore.** `deploy/backup.sh` keeps each night's dump with an archive of the Data Protection keys
beside it, and `deploy/verify-restore.sh` restores the newest dump into a scratch database and checks
it. A restore without the keys signs everyone out, and nothing else is lost. The procedure is in
[REMOTE_OPERATIONS, backups](../REMOTE_OPERATIONS.md#5-backups-and-the-half-that-is-usually-missing).

**Not automated, on purpose.**

- Approving sign-ups and every other admission decision: a person runs `admin`.
- Installing a release that changes the schema or the protocol: the timer parks it.
- Comparing the served panel with the published fingerprints: the pipeline publishes them; nothing
  compares them unless a person does
  ([how](../REMOTE_OPERATIONS.md#9-comparing-the-panel-with-its-build)).
- Copying backups off the machine.

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
- [Remote gateway](../src/Enactive.Remote.Gateway/Program.cs)
- [Remote host inside the desktop app](../src/Enactive.App.Ui/RemoteAccessService.cs)
