namespace Enactive.Core.Templates;

using Enactive.Core.Permissions;

/// <summary>
/// The templates the app ships with.
///
/// <para>Defined in code rather than written into %APPDATA% on first start. Seeding files means
/// deciding what to do when the user edits one and a later version changes it, and answering that
/// badly is how an application quietly overwrites somebody's work. A built-in is read-only, always
/// the current version, and <see cref="TaskTemplate.Duplicate"/> makes an editable copy the moment
/// anyone wants to change one — which is a better answer than a merge.</para>
///
/// <para>These are the saved tasks a person actually repeats. Nothing here is a chat prompt with a
/// title: each one has a workspace, bounds, and — where the job admits one — a deterministic check.
/// A task whose only possible verdict is an opinion says so by having no criteria at all.</para>
///
/// <para><b>What a deny list actually buys.</b> It narrows what the model is offered and permitted
/// to call BY NAME. It is not a sandbox. A template that may use <c>run_command</c> may, through the
/// shell, do anything the shell can do in that folder — including writing a file it was denied the
/// tool for. The honest ceiling for a task that must not write is <c>Observe</c>, and Observe
/// forbids commands too, which for most of these would remove the job. So: a deny list here states
/// intent and closes the easy path, and the real containment is the workspace root plus the
/// autonomy tier. Saying that plainly is better than implying a jail that is not there.</para>
/// </summary>
public static class BuiltinTemplates
{
    public static IReadOnlyList<TaskTemplate> All { get; } = new[]
    {
        // The one the whole feature was described by: a check to run every day without typing.
        new TaskTemplate(
            Id: "release-check",
            Name: "Release Check",
            Goal: "Check that this workspace is in a releasable state. Build it, run its tests, and "
                + "report exactly what the commands returned — the real output, never a summary you "
                + "wrote from memory. Do not change any source file.\n\n"
                + "Build command: {build_command}\nTest command: {test_command}",
            Version: 1,
            Description: "Build, test, and report what actually came back. Changes nothing.",
            Category: "DevOps",
            Parameters: new[]
            {
                new TemplateParameter("build_command", "Build command", TemplateParameterType.Text,
                    Description: "Run to build the project.", Default: "dotnet build"),
                new TemplateParameter("test_command", "Test command", TemplateParameterType.Text,
                    Description: "Run to test the project.", Default: "dotnet test")
            },
            // It reads and runs; it does not write. Observe would forbid the commands too, so the
            // ceiling stays at Execute and the deny list does the narrowing.
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "write_file", "edit_file", "move_file", "create_directory" }),
            SuccessCriteria: new[]
            {
                new SuccessCriterionDefinition("Builds", "dotnet build", 0),
                new SuccessCriterionDefinition("Tests pass", "dotnet test", 0)
            },
            Limits: new ExecutionLimits(MaxSteps: 6),
            ReviewRequired: true,
            Builtin: true),

        new TaskTemplate(
            Id: "investigate-build-failure",
            Name: "Investigate Build Failure",
            Goal: "A build has failed. Find out WHY, and report the cause with the evidence for it.\n\n"
                + "What failed: {what_failed}\n\n"
                + "Read whatever you need and run read-only commands. Do NOT fix anything and do not "
                + "change any file — the point of this task is the diagnosis. If you cannot establish "
                + "the cause, say so and say what you would need.",
            Version: 1,
            Description: "Diagnose a failing build. Reads and reports; changes nothing.",
            Category: "DevOps",
            Parameters: new[]
            {
                new TemplateParameter("what_failed", "What failed", TemplateParameterType.MultilineText,
                    Description: "The error, the pipeline step, or a link to the failing run.")
            },
            // Observe cannot run commands at all, which is most of how you diagnose a build - so the
            // ceiling is Execute and writing is denied outright instead.
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "write_file", "edit_file", "move_file", "create_directory" }),
            Limits: new ExecutionLimits(MaxSteps: 8),
            ReviewRequired: true,
            Builtin: true),

        new TaskTemplate(
            Id: "documentation-sync",
            Name: "Documentation Sync",
            Goal: "Check {docs_path} against what the code in this workspace actually does, and "
                + "correct what has drifted.\n\n"
                + "Change documentation only. Fix statements that are no longer true; leave wording "
                + "you merely disagree with alone. For anything you cannot verify from the code, say "
                + "so rather than guessing.",
            Version: 1,
            Description: "Bring a doc back in line with the code it describes.",
            Category: "Maintenance",
            Parameters: new[]
            {
                new TemplateParameter("docs_path", "Document or folder", TemplateParameterType.Path,
                    Description: "What to check, relative to the workspace root.", Default: "README.md")
            },
            // It edits prose, so it writes - but it has no business running anything, and git is
            // somebody's deliberate decision rather than a documentation task's.
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "run_command", "run_powershell", "git", "docker" }),
            Limits: new ExecutionLimits(MaxSteps: 8),
            ReviewRequired: true,
            Builtin: true),

        // ── Development ──────────────────────────────────────────────────────

        // The canonical one, and the only shape where a deterministic check gates a CHANGE rather
        // than a report: the fix is not done because the model says so, it is done when the thing
        // builds and the tests pass.
        new TaskTemplate(
            Id: "fix-bug",
            Name: "Fix Bug",
            Goal: "Fix this, in this workspace:\n\n{problem}\n\n"
                + "Find the cause before changing anything, and change the cause rather than the "
                + "symptom. Use edit_file for a change to part of a file — do not retype a file to "
                + "express a small edit. When you are done, say what was wrong and what you changed.\n\n"
                + "Build command: {build_command}\nTest command: {test_command}",
            Version: 1,
            Description: "Fix a described problem, gated by the build and the tests.",
            Category: "Development",
            Parameters: new[]
            {
                // Required with NO default on purpose: "fix the bug I have not described" is not a
                // task, so this template cannot be scheduled, and the console says which parameter
                // is missing rather than running something vague at 03:00.
                new TemplateParameter("problem", "The problem", TemplateParameterType.MultilineText,
                    Description: "What is wrong: the symptom, the error, how to see it happen."),
                new TemplateParameter("build_command", "Build command", TemplateParameterType.Text,
                    Description: "Run to build the project.", Default: "dotnet build"),
                new TemplateParameter("test_command", "Test command", TemplateParameterType.Text,
                    Description: "Run to test the project.", Default: "dotnet test")
            },
            // Version control is the person's decision, not a bug fix's: a fix that commits itself
            // takes away the review that was about to happen.
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "git", "docker" }),
            SuccessCriteria: new[]
            {
                new SuccessCriterionDefinition("Builds", "dotnet build", 0),
                new SuccessCriterionDefinition("Tests pass", "dotnet test", 0)
            },
            Limits: new ExecutionLimits(MaxSteps: 10),
            ReviewRequired: true,
            Builtin: true),

        new TaskTemplate(
            Id: "code-review",
            Name: "Code Review",
            Goal: "Review this and write your findings to {report_path}:\n\n{scope}\n\n"
                + "Report only what you can point at in the code — file and line. For each finding "
                + "say what breaks and under what input, not that it could be tidier. If you find "
                + "nothing worth reporting, write that; a review with invented findings is worse "
                + "than a short one.\n\n"
                + "Change nothing except the report.",
            Version: 1,
            Description: "Review changes and write the findings to a file. Changes nothing else.",
            Category: "Development",
            Parameters: new[]
            {
                new TemplateParameter("scope", "What to review", TemplateParameterType.MultilineText,
                    Description: "A diff, a folder, or what has changed recently.",
                    Default: "Everything that has changed since the last commit."),
                new TemplateParameter("report_path", "Write findings to", TemplateParameterType.Path,
                    Description: "Relative to the workspace root.", Default: "review.md")
            },
            // It writes its report, so write_file stays; editing and moving source do not.
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "edit_file", "move_file", "git", "docker" }),
            // No criteria, deliberately. A review's verdict IS an opinion, and there is no exit code
            // for one - so the run report will say "nothing verified this run", which is true.
            Limits: new ExecutionLimits(MaxSteps: 8),
            ReviewRequired: true,
            Builtin: true),

        new TaskTemplate(
            Id: "improve-tests",
            Name: "Improve Test Coverage",
            Goal: "Add tests to this workspace for {area}.\n\n"
                + "Write tests for behaviour that is not covered yet, in the style of the tests that "
                + "are already here. Each test must FAIL if the behaviour it describes is broken — "
                + "check that by breaking it temporarily and putting it back. Do not change any "
                + "source file to make a test pass.\n\n"
                + "Test command: {test_command}",
            Version: 1,
            Description: "Add tests for what is not covered, and prove they can fail.",
            Category: "Development",
            Parameters: new[]
            {
                new TemplateParameter("area", "What to cover", TemplateParameterType.Text,
                    Description: "A file, a class, or an area of behaviour.",
                    Default: "the behaviour with the least coverage you can identify from the tests already here"),
                new TemplateParameter("test_command", "Test command", TemplateParameterType.Text,
                    Description: "Run to test the project.", Default: "dotnet test")
            },
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "git", "docker" }),
            SuccessCriteria: new[]
            {
                new SuccessCriterionDefinition("Tests pass", "dotnet test", 0)
            },
            Limits: new ExecutionLimits(MaxSteps: 10),
            ReviewRequired: true,
            Builtin: true),

        // ── Maintenance ──────────────────────────────────────────────────────

        new TaskTemplate(
            Id: "update-dependencies",
            Name: "Update Dependencies",
            Goal: "Bring this workspace's dependencies up to date.\n\n"
                + "First run {check_command} and report exactly what it returned. Then update what "
                + "is safe to update, one thing at a time, building in between so a failure names "
                + "the package that caused it. Leave a major version bump alone and say why.\n\n"
                + "Build command: {build_command}\nTest command: {test_command}",
            Version: 1,
            Description: "Update what is outdated, gated by the build and the tests.",
            Category: "Maintenance",
            Parameters: new[]
            {
                new TemplateParameter("check_command", "How to list outdated packages",
                    TemplateParameterType.Text, Default: "dotnet list package --outdated"),
                new TemplateParameter("build_command", "Build command", TemplateParameterType.Text,
                    Default: "dotnet build"),
                new TemplateParameter("test_command", "Test command", TemplateParameterType.Text,
                    Default: "dotnet test")
            },
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "git", "docker" }),
            SuccessCriteria: new[]
            {
                new SuccessCriterionDefinition("Builds", "dotnet build", 0),
                new SuccessCriterionDefinition("Tests pass", "dotnet test", 0)
            },
            Limits: new ExecutionLimits(MaxSteps: 12),
            ReviewRequired: true,
            Builtin: true)
    };
}
