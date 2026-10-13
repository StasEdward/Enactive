# Tool Catalog — deferred tool exposure

Branch: `feature/tool-catalog` (from `master`)
Status: plan, not started
Origin: 2026-10 design discussion; the "smart agent routing" idea from agenticSeek
was reduced to the one thing Enactive actually lacks: a model should see only
the tools it needs, and be able to load the rest on demand.

## Problem

Every MCP server connected to a workspace puts all of its tool schemas into
every worker request. Schemas are 200–500 tokens each, so a few servers bloat
the context of every step, distract the model, and break prompt caching when
the set changes. Separately, §9ax left a hole: an implicit (text) tool call is
resolved against the *registry*, so a model can name a tool that was never
sent to it and the engine may run it.

## Goal

- Core tools (`read_file`, `write_file`, `list_dir`, `run_command`,
  `run_powershell`, git, docker) stay in every worker request as today.
- Deferred tools (MCP by default) are listed in the system prompt as a
  short catalog — one line per tool, no schema.
- A meta-tool `load_tools(names[])` returns the full definitions and adds
  them to the `tools` array of the following requests within the same step.
- Planner requests carry no tools at all.
- Implicit tool calls resolve only against the set actually sent to the model.
- Each step records what it was shown (`ToolsExposed` event).

Non-goals for this branch: capability tags, execution profiles, receipts /
evidence (those belong to FIX_PLAN_2), unloading tools mid-step.

## Working rules (unchanged)

- Each § below is a separate section in Docs/FIX_PLAN.md when implemented.
- Every test is proven RED first with a reversion script under `work/`
  (gitignored), then the fix lands, then the test goes GREEN.
- Run only the tests touched by the current section — never the whole suite.
- Commit only when Stas says so. Stas launches real runs himself.
- All code comments and doc text in English.

## Step 0 — map the plan to the real code (no edits)

Read and report mismatches with the type names used in this document:

| What to find | Why |
|---|---|
| `IToolRegistry` / `LoggingToolRegistry`, the tool descriptor type (name, description, schema) | where `Deferred` and `Summary` attach |
| How the Orchestrator builds the `tools` array per request, and the order it uses | §3 ordering + cache stability |
| Worker allowlist: type, storage in `AppSettings`, wildcard handling (`"*"` from the schema v2 migration) | §1 `mcp:<server>/*` pattern |
| How MCP servers register tools and how those tools are named | naming for the catalog |
| `TryRecoverImplicitToolCall` and the repair turn (§9ax) | §4 |
| `EventKind` and how events reach the log with run/step | §5 |
| `WorkerEditWindow` + `ToolToggle` view models | §6 |
| `FakeChatProvider` in tests/Enactive.Engine.Tests | §7 |

Output of step 0: a short "mismatch list" at the top of this file, then the
sections below are adjusted before any code is written.

## §1 Catalog model (Core + Tools)

- Tool descriptor gains `bool Deferred`. MCP tools default to `true`,
  built-in tools to `false`. Keep it a plain property; no new interface unless
  step 0 shows the descriptor is immutable and shared.
- `Summary` = first sentence of the description, trimmed to ~80 chars, computed
  once at registration. A tool with an empty description gets its name only.
- `ToolCatalog.For(worker)` = deferred tools ∩ worker allowlist, rendered as
  lines `name — summary`, sorted by name (ordinal).
- Allowlist pattern `mcp:<server>/*` matches every tool of that server. Exact
  names keep working. `"*"` keeps meaning "everything" (migration contract).
- If the catalog exceeds `CatalogCollapseThreshold` (default 40) lines, render
  one line per server instead and expose `list_tools(server)` as a second
  meta-tool. Build the threshold now, the collapsed mode can land later.

## §2 Meta-tool `load_tools`

- Signature: `load_tools(names: string[])`. Core tool, always exposed to
  workers whose catalog is non-empty; not exposed when the catalog is empty
  (nothing to load → the tool can only refuse, same rule as §9an).
- Validation per name, in order: present in this worker's catalog → not
  already loaded → step limit not reached. A failing name is reported in the
  result text and skipped; the call never throws. Unknown names are not
  echoed with suggestions (no "did you mean") — keep the surface small.
- Result text: the full definition of each accepted tool (name, description,
  JSON schema) so text-calling models (qwen) can use it from the transcript.
- Side effect: accepted tools are added to the step's `LoadedTools` set.
- `AppSettings.MaxLoadedToolsPerStep`, default 8, clamped 1..32 on save and
  at the use site (same pattern as `ReviewRetries`).
- `LoadedTools` is step-scoped: created when a step starts, dropped when it
  ends. A content-review retry that rolls the transcript back keeps the set
  (the model already knows those tools); an execution retry keeps it too.
  Parallel steps each have their own set.

## §3 Request assembly

- `tools` array per worker request = core tools in a fixed ordinal order,
  then `load_tools`, then loaded tools in load order appended at the END.
  The prefix never changes within a step, so Anthropic prompt caching keeps
  hitting.
- Catalog goes into the worker system prompt as its own section:

  ```
  ## Tools available on request
  Call load_tools with their names to get full definitions before use.
  If a catalog tool fits the task, load it first.
  gh.create_issue — create a GitHub issue in a repo
  jira.search     — JQL search over issues
  ```
- Planner requests: no `tools`, no catalog. Verify in step 0 whether the
  planner already receives tools today; if it does, this is the one-line
  change that removes them.
- Reviewer requests: unchanged (reviewer judges evidence, does not call tools).

## §4 Close the §9ax hole

- The set of tool names sent in a request is kept on the step context
  (`ExposedTools`).
- `TryRecoverImplicitToolCall` and the repair turn resolve the name against
  `ExposedTools`, not the registry. A registered-but-unexposed name yields a
  tool result "Tool 'X' is not available to this step. Load it with
  load_tools if it is in the catalog." and counts as a failed call for the
  step's incomplete-tracking (F5 rule).
- Native tool calls go through the same check; providers should never emit
  one for an unsent tool, but a mismatch is a bug worth a log line, not a
  silent execution.

## §5 `ToolsExposed` event

- New `EventKind.ToolsExposed`, emitted when a step's first request is built
  and again after every accepted `load_tools`.
- Payload: `{ step, worker, core: [...], catalog: [...], loaded: [...] }`.
- One log line with run/step correlation, source = Orchestrator.
- Timeline UI: fold these into the step card as a count ("tools: 7 core,
  12 in catalog, 2 loaded"), not as a list — same rule as folding
  AssistantDelta.

## §6 Settings / UI

- Worker Edit: a toggle per MCP server writes/removes `mcp:<server>/*` in the
  allowlist. Individual tool toggles stay for built-in tools.
- Migration: existing allowlists are not touched. A worker gets MCP tools
  only when a server toggle is switched on. Document this in the About/hint
  text so the sudden disappearance of MCP tools after update is explained.
- Settings → AI → General: `MaxLoadedToolsPerStep` with the "?" hint.

## §7 Tests (tests/Enactive.Engine.Tests, FakeChatProvider drives the real engine)

| # | Test | Reversion proves RED by |
|---|---|---|
| 1 | Catalog contains no tool outside the worker allowlist | catalog built from registry instead of allowlist |
| 2 | `load_tools` refuses a name not in the catalog, result text names it, no exception | validation removed |
| 3 | After an accepted `load_tools`, the NEXT request to FakeChatProvider contains that tool's definition | loaded set not appended |
| 4 | Limit: the (N+1)-th name is refused, first N accepted | clamp removed |
| 5 | A new step starts with an empty loaded set | set not reset |
| 6 | Implicit (text) call to a registered but unexposed tool is refused and recorded as a failed call | resolve against registry |
| 7 | Planner request has no `tools` | tools passed to planner |
| 8 | Core tool order is identical across two consecutive requests of one step | ordering by dictionary enumeration |
| 9 | `ToolsExposed` is recorded on first request and after each load, with correct counts | event not emitted |
| 10 | `mcp:<server>/*` matches that server's tools and nothing else; `"*"` still matches all | pattern matcher removed |

Each test must be seen failing with its reversion before the section is
marked done. A test green in both directions is not a test.

## §8 Measurement (Stas runs these)

1. A run with at least two MCP servers connected, worker on gemma4-12b:
   does the model call `load_tools` on its own when a catalog tool fits?
2. Prompt tokens per step from `UsageReported`, before and after, same task.
3. Cache-read ratio on Anthropic steps, before and after.

Record the numbers in Docs/FIX_PLAN.md next to the sections; they decide
whether the prompt phrase in §3 is enough or the worker prompt needs more.

## Open questions for step 0

- Does `read_file` etc. ever come from an MCP server with the same name as a
  built-in? If yes, catalog names must be server-prefixed always.
- Is the worker allowlist evaluated once per run or per step? The catalog
  must be built per step (worker can differ per step).
- Where does the Orchestrator keep per-step state today (for `LoadedTools`
  and `ExposedTools`)?
