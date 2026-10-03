# Enactive Wiki

**Product and engineering handbook · English · Verified against the repository on 8 September 2026. Remote access, the file tools, the settings schema and the console's arguments re-verified on 9 September 2026.**

Enactive is a desktop workspace for AI agents. You describe an outcome, choose a workspace and a worker, and let the application plan and execute work through tools. It records the actions, decisions, outputs, and final outcome so that you can inspect what happened and repeat a task later.

This wiki covers the desktop application, its shared execution engine, the console host, and remote access from a browser. It describes current implementation behavior. Recommendations are identified as recommendations; incomplete features are identified where they affect a workflow.

## Contents

| Page | What you will learn |
| --- | --- |
| [Application overview](Overview.md) | Purpose, core concepts, interface, and supported workflows |
| [Architecture and execution](Architecture.md) | Components, planning, tool execution, review, evidence, and outcomes |
| [Getting started](Getting-Started.md) | Prerequisites, build commands, initial configuration, and a first run |
| [Running tasks in the desktop app](Running-Tasks.md) | Manual tasks, template launches, approvals, staging, background work, and retries |
| [Console and unattended execution](Console.md) | Actual CLI syntax, configuration, reports, exit codes, and scheduling limits |
| [Templates](Templates.md) | Library, built-ins, parameters, editing, JSON, success criteria, and Global versus Workspace |
| [Settings](Settings.md) | Providers, Team, Phases, behavior toggles, persistence, and configuration scope |
| [Choosing models by phase and role](Models-and-Phases.md) | Model responsibilities, selection guidance, routing, recommended setups, and evaluation |
| [Remote access](Remote-Access.md) | Starting tasks from a browser: signing in, pairing a computer, adding and removing devices, and what a remote run may not do |
| [Remote security](Remote-Security.md) | What the remote service can and cannot see, the limits of its encryption, and how to compare the panel with its release |
| [Operations and troubleshooting](Operations.md) | History, logs, storage, MCP, recovery, and common problems |

## Recommended reading paths

- **First-time user:** Overview → Getting started → Running tasks → Templates.
- **Configuring an AI team:** Settings → Models and Phases → Running tasks.
- **Automating repeated work:** Templates → Console → Operations.
- **Contributor:** Architecture → Settings → Templates → Operations.

## Essential distinctions

| Concept | Meaning |
| --- | --- |
| Workspace | The folder in which a run operates |
| Task | The requested outcome; it can have several recorded attempts |
| Run | One attempt to carry out a task |
| Worker | A role with instructions, tools, an autonomy level, and a model policy |
| Provider | A configured model endpoint |
| Phase | A model responsibility: Plan, Execute, or Review |
| Template | A reusable task definition that resolves into a specification for one run |
| Global template | A template available across this user's workspaces |
| Workspace template | A project-local template that takes precedence over a Global template with the same ID |

## Documentation boundaries

The desktop and console share engine code but have different configuration roots. Desktop Providers, Team, Phases, and MCP settings do **not** configure the current console executable.

Remote access is a working connection in this checkout, with its own limits: you sign in with GitHub or Google, a computer is read and commanded only from the devices it trusts, and a task started from a browser may not run a shell or stage its changes. See [Remote access](Remote-Access.md) and [Remote security](Remote-Security.md).

Older design documents remain under [`Docs`](../Docs/README.md). Some describe historical states or intended behavior. When those documents disagree with current composition code, this wiki follows the code. Each detailed page includes implementation links for maintenance.

This wiki was prepared through source inspection and checks of documentation examples. It does not claim a live provider, GUI, or production deployment certification.
