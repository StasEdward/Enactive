# Getting Started

[Wiki home](README.md)

## Prerequisites

| Requirement | When it is needed |
| --- | --- |
| .NET SDK supporting `net10.0` | Building and running from source |
| Reachable model provider | Every AI execution |
| Ollama and a downloaded model | The default local setup and default console configuration |
| Provider API credentials | Authenticated remote endpoints |
| Project-specific tools | The task needs them: Git, Docker, language runtime, build tools, or test runner |
| Windows user profile | The documented desktop secret-persistence path uses Windows DPAPI |

`global.json` requests SDK `10.0.100`, allows prereleases, and permits `latestMajor` roll-forward. It is not a strict requirement to install only that exact patch version.

Commands below run from the repository root in PowerShell. Replace example workspace paths with your own folders.

## Build and test the application

```powershell
dotnet build Enactive.sln
dotnet test tests/Enactive.Engine.Tests/Enactive.Engine.Tests.csproj
```

This repository's engine tests use xUnit. The remote gateway has its own suite, which needs a MySQL 8 instance — `docker compose up -d` provides one, and `ENACTIVE_REMOTE_DB` points the tests at it. See [Remote access](Remote-Access.md).

## Prepare the default local provider

The repository seeds the local model name `qwen2.5-coder`. This is a compatibility/default value, not a claim that it is the strongest current choice.

```powershell
ollama pull qwen2.5-coder
```

Ensure Ollama is running. If it is not already managed by its desktop/background service:

```powershell
ollama serve
```

Use a model that can issue structured tool calls. Model choice and a more capable local candidate are covered in [Models and Phases](Models-and-Phases.md).

## Start the desktop app

```powershell
dotnet run --project src/Enactive.App.Ui
```

1. Select an existing workspace folder. The desktop will not create a misspelled folder on launch.
2. Open **Settings → AI → Providers** and inspect the local provider.
3. For native Ollama, select `OllamaNative` and use the local endpoint, normally `http://localhost:11434`. The adapter also accepts the seeded URL ending in `/v1` and normalizes it to the native API.
4. Discover or enter the exact model name. Adding a name to the catalog does not download it.
5. Open **AI → Team** and assign the model to the worker you will use.
6. Start with Plan unset, Review unset, and light/heavy routing unset to establish basic connectivity. Add independent review after the basic run works.
7. Save Settings.

## Run a first inspection task

Select **Reviewer** and submit:

```text
Read the top-level project files and explain how this repository is organized.
Identify the application entry points and the test project.
Cite the files you inspected. Do not change files.
```

Use **Run** or press Enter. A successful first inspection demonstrates that the provider can produce actual tool calls and consume results. Inspect the Execution cards rather than accepting a plausible answer without file reads.

## Run a first writing task

Select **Writer**, choose Execute, optionally enable **Stage changes**, and submit:

```text
Create onboarding-notes.md with a short repository overview based on the files
you can inspect. Identify any build instructions that still need verification.
Do not modify existing files.
```

Open Artifacts after the run. With staging enabled, inspect the diff and choose Apply or Reject. With staging disabled, tracked writes go directly to disk.

## Add review after connectivity works

Configure a second, stronger model under Providers, then bind it under **AI → Phases → Review model**. Every step then gets a verdict from it, documentation steps included. Repeat a small task and inspect the recorded review verdict.

Choose the [balanced setup](Models-and-Phases.md#recommended-configurations) as a starting point for ongoing development.

## Run from the console

```powershell
dotnet run --project src/Enactive.App.Console -- "Explain the top-level project structure without modifying files." "C:\work\sample"
```

The console defaults to the Developer worker, a local OpenAI-compatible endpoint, and interactive approvals for shell/Git/Docker calls. It does not load the desktop configuration. See the [full console reference](Console.md) before using templates or automation.

## Implementation references

- [SDK configuration](../global.json)
- [Desktop startup](../src/Enactive.App.Ui/Program.cs)
- [Settings defaults](../src/Enactive.App.Ui/AppSettings.cs)
- [Engine test project](../tests/Enactive.Engine.Tests/Enactive.Engine.Tests.csproj)
