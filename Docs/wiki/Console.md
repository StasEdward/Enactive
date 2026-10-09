# Console and Unattended Execution

[Wiki home](README.md)

## Scope of the console host

The console composes its runs exactly as the desktop does (`RunComposer`, see [ADR 0001](../adr/0001-one-run-composer.md)), from the same saved settings. It supports a free-text task, template execution, resuming an interrupted run, a timeline view, and an Inbox view. What it keeps of its own is who answers permission questions, where the events go (the terminal), and Ctrl+C. It is not a command-line front end to every desktop setting.

| Capability | Desktop | Current console |
| --- | --- | --- |
| Provider selection | Saved Providers list | Same saved Providers list |
| Model selection | Worker policy and phase bindings | Same |
| Team | Editable workers | Same saved team; `--role` picks one |
| Plan / Review / Execute routing | Phase bindings | Same phase bindings |
| MCP | Configured per-run connections | Same connections |
| Staging | Optional, in the foreground | Direct disk artifact store |
| Template library | Built-in + Global + Workspace | Same library resolution |
| Template criteria and limits | Yes | Yes |
| "Allow (workspace)" approvals | Honoured | Honoured when typed at the console, unless `--approve` is given; a scheduled run uses only "Allow (workspace, unwatched runs too)" |
| "Allow (session)" approvals | Honoured while the app runs | None - each invocation is its own process |

## Supported commands

Run from the repository root. The `--` separates `dotnet run` arguments from arguments passed to Enactive.

### Interactive free-text task

```powershell
dotnet run --project src/Enactive.App.Console -- "Create a Python script named list_files.py that lists files in the workspace." "C:\work\sample"
```

The first positional argument is the request and the second is the workspace. The workspace may also be passed through `--workspace`:

```powershell
dotnet run --project src/Enactive.App.Console -- "Explain this repository's entry points." --workspace "C:\work\sample"
```

The console creates the workspace directory if it does not exist. Unlike the desktop, a misspelled path can therefore create an empty folder.

With no request, the program uses its built-in example task to create a Python file-listing script. With no workspace argument, it uses the current working directory. Avoid relying on those defaults in automation.

When approval is needed, the console prints the complete decision and options. Pressing Enter accepts the displayed recommended/default option. End-of-input is treated as absence of an approver and refused. Enter a valid option ID explicitly; the parser falls back to the default for an unrecognized choice.

### Timeline

```powershell
dotnet run --project src/Enactive.App.Console -- timeline "C:\work\sample"
```

Prints recorded project history and memory without starting an AI task.

### Inbox

```powershell
dotnet run --project src/Enactive.App.Console -- inbox "C:\work\sample"
```

Prints workspace Inbox entries. Console task execution itself does not wire the desktop BackgroundRunner and does not automatically publish its outcome into that Inbox.

### Unattended template

```powershell
dotnet run --project src/Enactive.App.Console -- --template documentation-sync --workspace "C:\work\sample" --param "docs_path=Docs" --report "C:\work\reports\documentation-sync.txt"
```

Every template invocation is unattended, even when launched from an interactive terminal. There is no `--interactive` template option.

| Option | Meaning |
| --- | --- |
| `--template <id>` | Select a template by ID, not display name |
| `--workspace <path>` | Set the execution root |
| `--param id=value` | Supply one parameter; repeat the option for additional parameters |
| `--report <path>` | Also write the final textual report to a file |

Quote the entire `id=value` argument when it contains spaces. Relative report paths resolve against the process working directory, not automatically against the workspace. Parent directories are created for report output.

An unknown template or invalid/missing required parameter prevents execution and returns `64`. The program prints the resolution problems. Unknown parameter names are currently ignored by resolution; use the declared IDs carefully. Repeated IDs use the last supplied value.

### A multi-parameter invocation

```powershell
dotnet run --project src/Enactive.App.Console -- --template fix-bug --workspace "C:\work\sample" --param "problem=An empty search query throws an exception" --param "build_command=dotnet build" --param "test_command=dotnet test" --report "C:\work\reports\fix-bug.txt"
```

This demonstrates valid syntax. It is **not** a promise that Fix Bug can complete unattended with the current console policy: shell execution and required checks need approval and will be refused.

## Tier, role, and answers

Three arguments describe the conditions a run acts under. They exist so a behavior can be checked
from a command line instead of by driving the desktop by hand.

| Argument | Values | Default |
| --- | --- | --- |
| `--autonomy` | `observe`, `suggest`, `execute`, `autonomous` (or `0`-`3`) | `execute` |
| `--role` | a worker's id (`developer`, `reviewer`, `ops`, `writer`) or its role name, case ignored | `developer` |
| `--approve` | `allow`, `deny` | unset |

`--autonomy` uses the desktop application's own mapping, shared by both hosts. A check run here is
evidence about the product only if the console and the window agree on what `execute` means.

| Tier | Asks before |
| --- | --- |
| `observe`, `suggest` | nothing; the level itself limits what may run |
| `execute` | `run_command`, `run_powershell`, `git`, `docker`, `run_tests` |
| `autonomous` | nothing |

A tool whose own definition requires approval still asks at every tier, including `autonomous`.
`delete_file` is the current example.

`--role` decides which tools exist for the run at all. That is half of any permission question, and
the two halves are easy to confuse from outside: a task that reaches for a shell because the tool
it needed was never granted fails in the same shape as a task the policy refused.

`--approve` gives one fixed answer to every permission. Both directions are useful — `deny` checks
that a refusal actually stops an action, `allow` gets past a gate to see what is behind it — and
neither is a default.

A value none of these recognizes is refused with exit code `64` and the list of what was meant.
A typo is not rounded to the nearest tier in either direction.

`--resume` continues an interrupted run under the autonomy and role it was started with. Asking it
for another with `--autonomy` or `--role` is refused with exit code `64`: running the rest of the
steps under something other than what was typed, or other than what the run was started with, would
be wrong either way. Asking for the same is fine, so a scheduler line that names its level resumes as
before; and a run recorded without its level runs the rest under the one given.

## Unattended permission behavior

Without `--approve`, a free-text run asks at the terminal and a `--template` run uses the unattended
handler:

```text
Unattended + Ask = Deny
```

Templates can add restrictions but cannot grant more autonomy.

A tool somebody allowed with **Allow (workspace)** for this folder is answered "allow" without asking
when the command is typed at the console. A scheduled run has nobody watching it, so it uses only an
approval given with **Allow (workspace, unwatched runs too)**: such a tool is offered and used, and
every other tool that asks is kept back. `--approve` is the exception: it is the answer for that
invocation, given on purpose, and a remembered approval does not overrule it. The console does not
read the desktop workspace registry.

Consequences:

- File-only work may complete if its worker and template allow the necessary tools.
- Build/test workflows cannot run their required shell checks unattended in this composition.
- A required check that cannot execute is not counted as passed; inspect Incomplete and the report details.
- There is no benefit to adding an `Allow` field to template JSON: the template schema has no grant mechanism.

Those consequences describe a run left to the unattended handler. `--autonomy autonomous` or
`--approve allow` changes them, and both are deliberate instructions typed for that invocation
rather than something a template can grant itself.

The last line of output names the conditions alongside the outcome, so a result can be read without
knowing how it was invoked:

```text
RESULT outcome=Incomplete autonomy=autonomous role=developer
```

## Environment configuration

```powershell
$env:ENACTIVE_MODEL = 'qwen2.5-coder'
$env:ENACTIVE_OLLAMA_URL = 'http://localhost:11434/v1'
$env:ENACTIVE_STORE = 'sqlite'
$env:ENACTIVE_LOG_LEVEL = 'Debug'
```

| Variable | Default | Behavior |
| --- | --- | --- |
| `ENACTIVE_MODEL` | `qwen2.5-coder` | Model name for all built-in workers in this host |
| `ENACTIVE_OLLAMA_URL` | `http://localhost:11434/v1` | API base URL; the adapter appends `/chat/completions` |
| `ENACTIVE_STORE` | `sqlite` | Run, memory, and Inbox backend: `sqlite`, `json`, or `mysql` |
| `ENACTIVE_MYSQL` | Unset | ADO.NET connection string required for MySQL |
| `ENACTIVE_LOG_LEVEL` | `Debug` | `Trace`, `Debug`, `Info`, `Warn`, or `Error` |

Despite its environment variable name, the console endpoint uses the OpenAI-compatible adapter, not native Ollama. Native `num_ctx` and `think:false` behavior do not transfer from the desktop. The descriptor supplies no API key, and there is no current CLI API-key option.

The console creates an HTTP client with a five-minute timeout. This is separate from a template's run duration budget.

## Reports and exit codes

The report includes workspace, run/task identifiers, outcome and reason, recorded model, duration, checks, steps, artifacts, usage, decisions, and errors when available.

| Exit code | Meaning |
| --- | --- |
| `0` | Completed |
| `1` | Failed, or handled provider connection failure |
| `2` | Incomplete |
| `64` | Invalid invocation: an unknown template parameter, tier, role, or approval answer. Nothing ran |
| `130` | Cancelled, including handled Ctrl+C |

Configuration/startup exceptions outside the handled run path may terminate before a structured report is produced. The table is the host's explicit outcome mapping, not a guarantee for every process-level failure.

A failure to write `--report` is printed to stderr but does not change the task outcome exit code. A scheduler that requires the report file should verify its presence separately.

## Scheduling a suitable task

Use Windows Task Scheduler, cron, or a CI runner to invoke the console. There is no built-in durable scheduling service in the desktop app.

For a file-only documentation workflow, a PowerShell wrapper can preserve the process outcome:

```powershell
$env:ENACTIVE_MODEL = 'qwen2.5-coder'
$env:ENACTIVE_OLLAMA_URL = 'http://localhost:11434/v1'
Set-Location 'C:\source\Enactive'
dotnet run --project src/Enactive.App.Console -- --template documentation-sync --workspace 'C:\work\sample' --param 'docs_path=Docs' --report 'C:\work\reports\documentation-sync.txt'
$taskExitCode = $LASTEXITCODE
exit $taskExitCode
```

Use an explicit working directory and run under the intended account: Global templates live in that account's application-data folder. Ensure the endpoint and project dependencies are available without an interactive login. For a stable installation, publish the console and point the scheduler at that executable rather than building source for every invocation.

## Implementation references

- [Complete CLI and composition](../src/Enactive.App.Console/Program.cs)
- [Autonomy tiers, shared with the desktop](../src/Enactive.Agents/AutonomyTiers.cs)
- [Unattended decisions](../src/Enactive.Agents/UnattendedDecisionHandler.cs)
- [Report and exit-code mapping](../src/Enactive.Core/RunReport.cs)
- [Template resolution](../src/Enactive.Core/TemplateResolution.cs)
