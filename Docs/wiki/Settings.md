# Settings

[Wiki home](README.md)

## Configuration layers

| Layer | Controls | Persistence |
| --- | --- | --- |
| Application settings | Providers, workers, phase bindings, global instructions, AI toggles, MCP, window behavior | `%APPDATA%\Enactive\settings.json` |
| Workspace preferences | Selected role, autonomy, staging, recent workspace information | `%APPDATA%\Enactive\workspaces.json` |
| Workspace approvals | Standing desktop tool approvals keyed by workspace identity | `%APPDATA%\Enactive\permissions.json` |
| Templates | Reusable goals, parameters, ceilings, checks, limits | Global or Workspace template files |
| Resolved run specification | One template resolved with concrete answers and permissions | Stored with a run |
| Environment | Storage backend, logging, console model/endpoint, initial desktop defaults | Process environment |

There is no independent Workspace Providers/Phases file in the current desktop implementation. Global instructions and phase bindings are application-wide. “Workspace versus Global” in the template editor selects a template's storage scope, not a separate full application-settings profile.

## Saving and cancelling

Most settings are edited on a copy and take effect through the outer **Save** action. Cancel discards those unsaved edits. Provider/worker/MCP sub-editors update that draft; finish by saving the main Settings window.

Template files are the exception: their editor saves immediately, and deletion is immediate after its confirmation. Outer Cancel cannot undo those file operations.

If saving fails, inspect the reported error before closing the editor. Secret-protection failures must not be treated as successful saves.

## General, Account, and About

**General** includes startup behavior and what happens when the main window closes. Close to tray is enabled by default. With no system tray available, closing exits because the window has nowhere to hide. Quitting with work in flight requires attention because background work lives in the same process.

**Account** is currently a placeholder. Enactive has no desktop account system of its own; provider credentials live under AI → Providers.

**About** shows application/build/runtime information and component versions, useful when diagnosing mixed or stale application files.

## AI → Providers

A provider is a named endpoint with a transport adapter, authentication, and a catalog of model IDs.

| Field | Guidance |
| --- | --- |
| ID | Stable unique key used in model references; changing it affects references |
| Display name | Human-readable provider label |
| Kind | `OllamaNative`, `Anthropic`, or `OpenAiCompatible` |
| Base URL | API base for the selected adapter |
| API key | Credential entered in the editor and protected when saved |
| Headers | Provider-specific HTTP headers; use only where needed |
| Models | Exact endpoint model names, discovered or entered manually |
| Max output tokens | Optional per-provider response cap where the adapter supports it |

### Adapter behavior

| Kind | Request interface | Practical configuration |
| --- | --- | --- |
| OllamaNative | Native `/api/chat` | Local URL such as `http://localhost:11434`; native context/thinking controls |
| Anthropic | Messages API | Base such as `https://api.anthropic.com`; API credentials and exact model ID |
| OpenAiCompatible | `<BaseUrl>/chat/completions` | API prefix such as `https://api.openai.com/v1`, or a compatible local/remote service |

Model discovery populates a catalog. It does not install local models or prove compatibility with streaming, tool calls, or every request field Enactive sends. Validate a candidate with a small real run before using it across phases.

OpenAI-compatible does not mean support for the Responses API. The current adapter sends Chat Completions fields, including `temperature` when requested and `max_tokens` when configured. An endpoint/model that rejects those fields needs compatibility work; a model ID alone cannot solve it.

### Output caps

- OpenAI-compatible: request-level `MaxTokens`, otherwise the provider setting, is sent as `max_tokens`.
- Anthropic: uses a default output budget of 32,000 when no override applies, and can retry after learning a lower supported cap from an API error. It also retries a rejected temperature parameter without that field.
- Native Ollama: the current adapter does not map the provider Max output tokens field to `num_predict`.

Do not confuse response output caps with `NumCtx` (context capacity) or template `MaxTokens` (cumulative run budget).

### Secret storage

Provider API keys are saved as DPAPI-protected values; the plaintext API key property is excluded from serialization. General provider Headers are ordinary settings data, unlike the encrypted MCP header/environment dictionaries. Do not place secrets in general custom headers expecting the API-key protection to cover them.

Windows DPAPI protection is tied to the user context. Copying settings to another machine/account may require re-entering credentials. Keep keys out of templates and Global instructions.

## AI → Team

Workers combine instructions, tools, permission level, a preferred model, and an optional fallback. Models are referenced as:

```text
providerId/model-name
```

For example, `ollama/qwen3-coder:30b` identifies a model under the configured provider with ID `ollama`. The provider ID is an Enactive setting, not part of the upstream model name.

| Built-in worker | Default tools | Default level |
| --- | --- | --- |
| Developer | File inspection/editing, directories/moves, shell, Git, Docker | Execute |
| Reviewer | Read, search, list | Observe |
| Ops | Read/search/list, shell, Git, Docker | Execute |
| Writer | Read/search/list, write/edit, directories/moves | Execute |

The worker's tool allowlist is authoritative: an empty list permits no tools; `*` permits all registered tools. MCP additionally supports `mcp__*`, a server prefix such as `mcp__example__*`, or an exact discovered tool name.

Worker instructions are augmented at runtime with shared honesty rules, optional read-back guidance, and Global instructions. Put role-specific behavior in the worker and project/task-specific requirements in a template or request.

Fallback addresses supported execution-provider failures. It does not automatically replace a weak model because a review failed and does not configure Plan/Review failover.

## AI → Phases

| Setting | When selected | When blank |
| --- | --- | --- |
| Plan model | Uses that model for request classification/planning | Uses the worker's base model |
| Review model | Gives every step a short verdict from that model | Skips model review |
| Execute light | Uses that model for trivial DAG steps | Uses the worker model for those steps |
| Execute heavy | Uses that model for complex DAG steps | Uses the worker model for those steps |

Normal execution is configured in **Team → worker model**, not in a separate normal-phase dropdown. Light/heavy routing does not alter role permissions. See [Models and Phases](Models-and-Phases.md) for selection guidance.

`ReviewRequired` in a template is currently metadata only. The Review binding is the actual on/off switch in both quick-action and planned desktop execution.

## AI → General

| Setting | Default | Effect and recommended use |
| --- | --- | --- |
| Context length (`NumCtx`) | Blank/null | Requests native Ollama execution context size. Increase only when necessary and supported by available memory |
| Global instructions | Empty | Added to every worker's instructions; use for stable cross-project conventions |
| Disable local reasoning | On | Native Ollama execution requests use `think:false`; useful when thinking consumes turns without useful output |
| Verify writes | On | Adds instructions to read written real data back; useful for unreliable writers, costs extra work |
| Revert rejected steps | On | Attempts to restore tracked writes after final rejection, preserving newer conflicting edits |
| Review retries | 1 | One extra attempt after rejection; allowed range 0–5 |
| Allow implicit tool calls | Off | Can execute tool calls inferred from assistant text; leave off for normal structured-tool models |
| Parallel steps | 1 | Number of independent plan steps that may overlap |

Verify writes is an instruction to the worker, not an unconditional host read-back after every write.

`NumCtx` and Disable local reasoning are passed through the execution loop. Separate Plan/Review requests do not expose equivalent per-phase context/thinking configuration in the current UI. These settings do not configure OpenAI reasoning effort or Anthropic extended/adaptive thinking.

Parallelism helps when branches are independent and provider capacity permits overlap. Multiple steps on one constrained local endpoint may queue or compete for memory. Keep 1 until you have a representative task that benefits.

## AI → MCP

Configure external tool servers here, then grant the intended worker MCP access under Team. Enabled servers connect for each new desktop run. An unavailable enabled server can prevent the run from starting.

MCP tools require Execute-level access and normally ask on every call. Their external effects are not covered by built-in staging/revert. See [MCP operations](Operations.md#mcp-external-tools) for the full workflow.

## Web

Whether tasks may read the web. Off until you turn it on.

| Setting | Effect |
| --- | --- |
| **Let tasks read the web** | Adds `fetch_url`: reads a page by its address and returns its title and text, without markup, scripts or styles. Public addresses only — not this computer and not its network. |
| **Search server** | Adds `web_search`, through a [SearXNG](https://docs.searxng.org/) server you run, e.g. `http://localhost:8888`. In its `settings.yml`, add `json` under `search.formats`. Blank: no search. Setting one up with Docker: [Operations → Web](Operations.md#web-tools-and-searxng). |
| **Use without asking each time** | Off: every page and every search asks first, naming the address or the query, and a task nobody is watching is not offered the tools. On: they are used without a question, scheduled tasks included. |

The tools are not in the default roles. Give them to a role under **AI → Team**; until a role has them, the run says that tools are registered and offered to nobody.

What the tools bring back is marked as text from the web — data to work with, not instructions. A page is read up to 2 MB and returned up to 12,000 characters unless the call asks for more (at most 50,000); a cut always says so. A search server that does not answer is reported as unavailable, never as an empty result.

Asking for a page sends its address out of this computer, and a search sends its query: both are written by the model from the task. Turn the question off only for tasks whose requests you trust.

The limits, what the address check refuses, and troubleshooting are in [Operations → Web](Operations.md#web-tools-and-searxng).

## Environment defaults and migration

The desktop seeds its initial model/endpoint from `ENACTIVE_MODEL` and `ENACTIVE_OLLAMA_URL` when a usable saved settings file is absent. Existing Providers/Workers/Bindings are authoritative after migration; editing environment variables is not a reliable way to override saved desktop bindings.

`ENACTIVE_WORKSPACE` supplies a desktop workspace startup value. Storage/logging variables apply to the components that read them; the console-specific interpretation is documented in [Console](Console.md#environment-configuration).

Current settings schema version is 6. A file that states no version and lists no provider is from before providers and workers existed, and gets a local Ollama provider and the default team on first load — as does a first run with no file. A file that states a version is read as it stands: providers and workers removed and saved stay removed. Migrations preserve older tool-access behavior explicitly and hand a saved worker the tools it already had the capability for under another name — `edit_file`, `create_directory`, `move_file` and `search_files` to a worker that could already write or read, and `copy_file` to one that could do both. `delete_file` is deliberately excluded: it is not a capability anybody already held, so it is added in Settings by somebody who decided to, never by a migration. Startup repair can retain a `settings.before-repair-*.json` copy when repairing problematic settings.

Old legacy fields such as `MultiAgent`, `ReasonerModel`, and the single `BaseUrl`/`Model` are migration inputs. Configure the current Providers/Workers/Bindings schema rather than trying to control a migrated installation through those fields.

## Remote access

| Field | Meaning |
| --- | --- |
| Answer this computer's remote gateway | Whether this computer connects to the gateway at all |
| Connection code | The `enactive-connect:` code the panel shows when the computer is registered under Computers; **Connect** applies it at once |
| Gateway, Computer id, Token | What the last code stored: shown, not edited |
| Test connection | Greets the gateway with what is stored, and publishes this computer's workspaces |
| Trusted devices | The browsers that hold this computer's keys, each with **Remove**, and **Add a device** for a one-time link and QR code |

The token is stored with the same operating-system user-level protection as the provider keys, and
is never written to the settings file in clear text; the code itself is not kept. Changes take
effect without restarting the application: the connection is re-established with the new settings.

**Test connection is not only a check.** It connects and syncs, which is what makes this computer's
workspaces selectable in the panel. A computer that has never synced appears with no workspaces to
choose.

See [Remote access](Remote-Access.md) for pairing, devices, the panel, permissions, and what a remote run may not do.

## Implementation references

- [Settings schema, defaults, migration, and persistence](../src/Enactive.App.Ui/AppSettings.cs)
- [Tool implication rules used by the migration](../src/Enactive.Agents/WorkerTools.cs)
- [Settings UI](../src/Enactive.App.Ui/SettingsWindow.axaml)
- [Workspace preferences](../src/Enactive.Workspace/WorkspaceRegistry.cs)
- [Model router](../src/Enactive.Agents/ModelRouter.cs)
- [OpenAI-compatible adapter](../src/Enactive.Providers/OpenAiCompatibleProvider.cs), [Ollama adapter](../src/Enactive.Providers/OllamaNativeProvider.cs), [Anthropic adapter](../src/Enactive.Providers/AnthropicProvider.cs)
