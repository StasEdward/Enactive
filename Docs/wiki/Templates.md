# Templates

[Wiki home](README.md)

## What a template contains

A template is a reusable definition of a task: what to accomplish, which values the user must supply, what restrictions apply, and what checks decide whether it finished. It is stored as JSON or shipped as a built-in definition.

It does not execute by itself, contain a fixed plan, select phase models, or create a schedule. The host resolves it against a workspace, then starts the ordinary planning/execution pipeline.

## Library versus editor

| Location | Purpose |
| --- | --- |
| Main window → Templates | Choose a template, fill inputs, inspect constraints, and launch a foreground run |
| Settings → AI → Templates | Create, customize, duplicate, edit, or delete template files |

The library follows the active workspace. The list shows origin, version, and whether the entry replaces another copy. Reopen/reload the library after editing files externally.

## Global versus Workspace

| Origin | Location | Availability | Best use |
| --- | --- | --- | --- |
| Built-in | Compiled into `BuiltinTemplates` | Every workspace | Shipped starting points |
| Global | `%APPDATA%\Enactive\templates\<id>.json` | All workspaces for this user/profile | Personal reusable workflows with project-specific inputs |
| Workspace | `<workspace>\.enactive\templates\<id>.json` | That workspace | Repository-specific commands, conventions, and checks |

Global means available across the user's workspaces. It does not mean synchronized across machines, shared with all OS users, or published to a team. Workspace files can be committed to version control and shared with a repository, but they are not committed automatically.

### Resolution priority

```text
Workspace copy > Global copy > Built-in definition
```

Matching uses the template ID, case-insensitively, rather than the display name. The highest-priority copy replaces the entire lower-priority definition; fields are not merged.

For example, suppose the Global library contains `release-check`, while one repository contains its own `release-check`. That repository runs its local definition. Other repositories continue using the Global definition. Editing the Global file will not affect the repository with the override.

Deleting a Workspace override reveals the Global copy if present, otherwise the built-in. Deleting a Global override reveals a built-in of the same ID if one exists. Removing an override does not delete lower-priority definitions.

### Which scope should you choose?

Choose **Global** when the workflow is yours and reusable: “Synchronize documentation” with a `docs_path` parameter, for example. Keep project-specific commands as parameters.

Choose **Workspace** when correctness depends on this repository: a particular solution path, integration-test command, report location, or team convention. A local file can evolve in the same pull request as the code it checks.

Scope controls storage and precedence, not permissions. A Workspace template has no greater authority than a Global template.

## Create a template

1. Open **Settings → AI → Templates → New template**.
2. Enter a stable lowercase ID, a readable name, and a goal.
3. Add parameters for values that vary between runs.
4. Optionally select a worker ID.
5. Add permission restrictions and useful execution limits.
6. Add deterministic success criteria when the outcome can be checked by a command.
7. Choose the destination scope explicitly.
8. Save in the template editor, then launch it through the main Templates library.

**Save behavior:** template Save writes a file immediately. Cancel in the outer Settings window does not undo saved templates or deletions.

**Scope-selection caveat:** the current editor initializes scope to the first offered option, Global. It does not reliably infer the original file's scope when opening an existing template. Always check the Scope control before saving a Workspace edit.

Saving to another scope writes a copy there; it does not move or delete the original. If the original Workspace copy remains, it continues to shadow the new Global copy.

## Customize, duplicate, and reset

| Operation | Result |
| --- | --- |
| Edit a built-in | Opens an editable draft; saving the same ID creates an override file |
| Duplicate | Creates a new ID, drops the built-in flag, and starts at version 1 |
| Edit a user template | Saves the definition to the selected scope |
| Reset an override of a built-in | Deletes the override so the lower-priority definition becomes visible |
| Delete | Removes that file from its origin scope; historical runs remain |

The built-in source is never overwritten by the editor. Use a new ID for an independent workflow and the same ID for an intentional override.

Version is stored metadata, not an automatically incremented revision. The current form preserves the original version, and the JSON-to-form synchronization does not provide a dependable version-editing workflow. To intentionally increment `Version`, edit the stored JSON file directly and reload the library. Frozen run snapshots still preserve the exact definition used even if authors leave Version unchanged.

## Field reference

| Field | Meaning |
| --- | --- |
| `Id` | Stable identifier; lowercase letter followed by lowercase letters, digits, `_`, or `-` |
| `Name` | Display title |
| `Goal` | Task instructions with optional `{parameter_id}` placeholders |
| `Version` | Positive integer, default 1 |
| `Description` | Short explanation for the library |
| `Category` | Organizational label |
| `Parameters` | Typed input definitions |
| `Permissions` | A ceiling: `MaxLevel`, `AskBefore`, and `Deny` |
| `SuccessCriteria` | Named commands and expected exit codes |
| `Limits` | Optional `MaxSteps`, `MaxTokens`, and `MaxDurationSeconds` |
| `WorkerId` | Preferred worker ID; omit to use host selection/default |
| `ReviewRequired` | Stored intent; currently not enforced by host composition |
| `Builtin` | Shipped-definition marker; user files should use false or omit it |

The filename convention is `<Id>.json`. The loader uses the ID inside the JSON, so keep filename and ID aligned and avoid two files with the same ID in one folder.

## Parameters

Every parameter value is ultimately represented as a string. The type controls input presentation and validation.

| Type | Input and validation |
| --- | --- |
| `Text` | Single-line text |
| `MultilineText` | Multi-line text for problems, requirements, or excerpts |
| `Boolean` | Checkbox in the UI; string `true` or `false` in the specification |
| `Integer` | Integer string validated by parsing |
| `Choice` | Dropdown; at least two choices; values must match a choice exactly |
| `Path` | Text input describing a path; no existence or workspace-containment validation at parameter resolution |

Each parameter has `Id`, `Name`, `Type`, optional `Default`, optional `Description`, and `Required` (true by default). Choice adds a `Choices` array.

### Defaults and omitted values

- An empty supplied string falls back to the default.
- A missing required value without a default blocks resolution.
- An omitted optional value without a default resolves to an empty string.
- The desktop trims textual inputs. Console values are taken from the argument text.
- In the desktop, an unset Choice initially selects its first option; a Boolean yields true or false. A console invocation must supply missing required values or rely on actual template defaults.

### Placeholder substitution

Declared placeholders are substituted in **Goal** and **each criterion's Command**. Criterion names and descriptions are not substituted. Replacement is one pass; inserted text is not recursively evaluated as another template.

For example:

```text
Goal: Review {scope} and write findings to {report_path}.
Criterion command: {test_command}
```

Brace text such as `{0}` or JSON syntax is not a general expression language. A template that declares parameters rejects unknown placeholder-shaped names in its goal. The validator does not provide the same unknown-placeholder check for criterion commands, so check their spelling carefully.

Substitution does not shell-escape parameter values. If a parameter is inserted into a command, it is part of the command text that will be executed. Quote path arguments for the intended shell, and inspect resolved commands before approval.

## Permission ceiling

```json
{
  "MaxLevel": "Execute",
  "AskBefore": ["write_file"],
  "Deny": ["git", "docker"]
}
```

`MaxLevel` can lower the workspace's automatic execution tier. `AskBefore` adds approval requirements. `Deny` adds absolute tool-name restrictions. There is no Allow list in the ceiling.

The resolver retains the workspace Allow list and unions AskBefore/Deny entries. Deny takes precedence. Tool policy matching is exact and case-insensitive; a template entry such as `mcp__*` is not a general wildcard deny. MCP prefix patterns belong to worker allowlist handling, which is a different mechanism.

Tool restrictions apply by name. Denying `write_file` while allowing shell execution does not stop the shell from writing files. A goal saying “only change the report” is also not a path-specific permission rule.

## Success criteria

```json
{
  "Name": "Tests pass",
  "Command": "{test_command}",
  "ExpectedExitCode": 0,
  "Required": true
}
```

The command runs through `run_command` after execution and is judged by its actual exit code. The default expected code is 0. Required defaults to true.

Use required criteria for completion conditions and optional criteria for advisory checks. A denied/unavailable required check is unknown, not passed. A checker that exits 0 without running meaningful tests can still pass the criterion: Enactive cannot infer that a command name selected the correct test harness.

On Windows, criteria run through cmd rather than the PowerShell tool. Use shell-compatible commands or explicitly invoke the required interpreter. Avoid attaching shell criteria to a template that denies `run_command`.

## Limits

Set positive integers or leave a limit absent/null. Zero is not “unlimited” in the template validator.

| Limit | What it bounds |
| --- | --- |
| `MaxSteps` | Dispatch of plan steps; a quick action is not counted as a DAG step |
| `MaxTokens` | Cumulative usage reported across model phases |
| `MaxDurationSeconds` | Elapsed run time checked at execution boundaries |

The current form parses invalid/nonpositive limit text as an omitted limit. Verify the resulting JSON if a limit matters. These are not hard in-flight cancellation or billing caps; see [Architecture](Architecture.md#limits-are-scheduling-boundaries).

## Complete example: a portable build and test check

Save this as `portable-release-check.json` in the scope you choose. It demonstrates parameterized goal text **and** criteria:

```json
{
  "Id": "portable-release-check",
  "Name": "Portable Release Check",
  "Version": 1,
  "Description": "Build and test this workspace using explicitly supplied commands.",
  "Category": "DevOps",
  "Goal": "Check this workspace for release readiness. Run {build_command} and {test_command}. Report actual outputs and failures. Do not edit source files or commit changes.",
  "Parameters": [
    {
      "Id": "build_command",
      "Name": "Build command",
      "Type": "Text",
      "Required": true,
      "Default": "dotnet build"
    },
    {
      "Id": "test_command",
      "Name": "Test command",
      "Type": "Text",
      "Required": true
    }
  ],
  "Permissions": {
    "MaxLevel": "Execute",
    "Deny": ["write_file", "edit_file", "move_file", "create_directory", "git", "docker"]
  },
  "SuccessCriteria": [
    { "Name": "Builds", "Command": "{build_command}", "ExpectedExitCode": 0, "Required": true },
    { "Name": "Tests pass", "Command": "{test_command}", "ExpectedExitCode": 0, "Required": true }
  ],
  "Limits": { "MaxSteps": 6, "MaxTokens": 60000, "MaxDurationSeconds": 900 },
  "WorkerId": "developer",
  "ReviewRequired": true,
  "Builtin": false
}
```

Run this interactively in the desktop so you can approve commands. For this repository, a useful test value is `dotnet test tests/Enactive.Engine.Tests/Enactive.Engine.Tests.csproj`. A different repository should supply its own real test command.

The 60,000-token and 900-second limits are example starting values, not measured sizing recommendations. Bind a Review model separately. The current console cannot approve this template's required shell checks unattended.

## Built-in library

| ID | Purpose and parameters | Current deterministic checks |
| --- | --- | --- |
| `release-check` | Build/test report; `build_command`, `test_command` | Fixed `dotnet build`, `dotnet test` |
| `investigate-build-failure` | Diagnosis; required `what_failed` | None |
| `documentation-sync` | Correct drift; `docs_path`, default `README.md` | None |
| `fix-bug` | Fix a described problem; required `problem`, build/test commands | Fixed `dotnet build`, `dotnet test` |
| `code-review` | Findings report; `scope`, `report_path` | None |
| `improve-tests` | Add uncovered behavior tests; `area`, required `test_command` | None (version 3) |
| `update-dependencies` | Incremental dependency updates; `check_command`, build/test commands | Fixed `dotnet build`, `dotnet test` |

Several built-ins parameterize commands in the goal while retaining literal .NET commands in their criteria. Changing a build/test input therefore does **not** change those literal checks. Customize their criteria to `{build_command}` / `{test_command}` when adapting them to another stack, as shown above.

Improve Test Coverage declares no criteria of its own (since version 3). It used to declare `dotnet build`, which fails on a non-.NET project; the engine now checks for new build errors itself, for every kind of project it recognizes. Requiring all tests to pass would fight the task, which may uncover existing broken behavior and forbids changing source just to make tests green - a proposed test check that already fails before the work is dropped. A template's own criteria also fix the list the review of the final checks may return, so a template that names its test command in its goal and declares a different check leaves that review nothing it can add.

Code Review writes a report. The default Reviewer worker cannot write files or run Git, so use Developer or a deliberately configured report-writing worker for that template. A template's category/name does not automatically select a matching worker.

## Editing JSON directly

Use valid JSON with enum names as strings. The editor synchronizes Form and JSON on tab changes; invalid JSON keeps you in the JSON view with an error. For fields not reliably preserved by form round-tripping, edit the file externally and reload.

Malformed/unreadable files are skipped when listing the library. Loading primarily checks that the ID is usable; full template validation happens on save or resolution. A file appearing in the list is therefore not proof that it can launch.

Template files are app configuration under the reserved `.enactive` directory. Built-in workspace tools refuse access to that reserved state path; use the template editor or an external text editor to maintain them.

## Implementation references

- [Schema and validation](../src/Enactive.Core/Templates.cs)
- [Parameter resolution and snapshots](../src/Enactive.Core/TemplateResolution.cs)
- [Storage and priority](../src/Enactive.Workspace/TemplateStore.cs)
- [Built-in definitions](../src/Enactive.Core/BuiltinTemplates.cs)
- [Editor behavior](../src/Enactive.App.Ui/ViewModels/TemplateEditViewModel.cs)
- [Settings template actions](../src/Enactive.App.Ui/ViewModels/TemplateSettingsViewModel.cs)
