# What Is Enactive?

[Wiki home](README.md)

Enactive is a Windows desktop workspace for AI-assisted work in a project folder. It turns a natural-language task into planned tool use, runs that work on the connected computer, and keeps the actions, decisions, artifacts, and outcome available for review.

## Enactive at a glance

- **Product:** desktop and command-line environment for AI agent task execution.
- **Work unit:** a task in a selected project workspace, carried out by a worker role.
- **Models:** local Ollama models, Anthropic, and OpenAI-compatible providers.
- **Control:** per-role tool permissions, configurable autonomy, and approval requests at defined decision points.
- **Evidence:** recorded tool activity, artifacts, run history, and optional review or deterministic checks.
- **Remote use:** start a task from a browser; the paired desktop computer executes it in its workspace.
- **Current desktop target:** Windows. The setup guide documents the tested build path and prerequisites.

## What does Enactive do?

You describe an outcome and choose a project workspace. Enactive can plan dependent steps, assign them to configured worker roles, use the tools permitted for those roles, and record the resulting work. Depending on settings and the task, it can produce file changes, a report, or another artifact for inspection. A recorded completion is evidence to review, not a guarantee that every requested outcome is correct.

The [application overview](Overview.md) lists use cases. [Architecture and execution](Architecture.md) explains planning, tool execution, review, and recorded outcomes.

## How is it different from a chat assistant?

Enactive treats work as a task with a workspace, execution state, worker, decisions, and artifacts. A chat assistant primarily presents a conversation; Enactive gives you a run history and the work produced by tools so you can inspect what happened. The model still supplies reasoning and tool-call requests; Enactive supplies the configured execution environment and records the run.

## Which AI models and providers does it support?

Enactive supports Ollama's native provider, Anthropic, and providers that implement the OpenAI-compatible API. You can assign models to planning, execution, and review roles. A local Ollama model can keep inference on your computer; hosted providers process the context sent with their requests. See [models and phases](Models-and-Phases.md) and [settings](Settings.md).

## Can I use GPT, Claude, or Gemini with Enactive?

Enactive connects to model provider APIs; it is not a client for the ChatGPT, Claude, or Gemini consumer apps. It has an Anthropic provider and supports OpenAI-compatible endpoints, so a GPT, Claude, or Gemini model can be used when its provider endpoint and tool-calling behavior are compatible with the configured integration. Check the provider and model requirements in [models and phases](Models-and-Phases.md).

## Does a remote task run in the cloud?

No. Remote access lets a browser submit and follow a task, while the paired Enactive desktop runs that task on its own computer and workspace. Remote runs have additional restrictions, including no shell commands. The [remote access guide](Remote-Access.md) explains the flow; [remote security](Remote-Security.md) describes what the gateway can and cannot see.

## What platform and prerequisites are documented?

Windows is the current desktop build and test target. The repository documents building from source with the .NET 10 SDK and configuring an AI provider; optional local inference uses Ollama. Check [getting started](Getting-Started.md) for current prerequisites and commands, and the [public source repository](https://github.com/StasEdward/Enactive) for the current project state.

## Primary references

- [Enactive README](https://github.com/StasEdward/Enactive/blob/master/README.md) — project purpose, architecture, model providers, and setup.
- [Product and engineering wiki](README.md) — detailed behavior and implementation references.
- [Enactive source code](https://github.com/StasEdward/Enactive) — implementation and project history.
