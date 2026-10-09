namespace Enactive.Core.Templates;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;

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
            Version: 2,
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
            // The commands the person gave, not dotnet's: with a test command of their own, these checks ran
            // dotnet anyway and judged a workspace by a tool it may not have.
            //
            // The tests are RUN, not required to pass. This task reports on the workspace and may not change it,
            // so failing tests are the finding, not a fault in the work - and a check demanding exit 0 failed the
            // run on 2026-10-05 for exactly the report that was asked for, then sent a repair to make the tests
            // pass with every file tool denied. Exit 1 is how a test runner says "some failed"; anything else -
            // a runner that is not there, a crash - still fails. Some runners also exit 1 when the build under
            // them breaks, which is why "Builds" stays required beside it: that one says whether there was
            // anything to test.
            SuccessCriteria: new[]
            {
                new SuccessCriterionDefinition("Builds", "{build_command}", 0),
                new SuccessCriterionDefinition("Tests ran", "{test_command}", 0) { ExpectedExitCodes = [0, 1] }
            },
            Limits: new ExecutionLimits(MaxSteps: 6),
            ReviewRequired: true,
            Builtin: true)
        {
            // Unconditional: the goal is "build it, run its tests", and both parameters are
            // commands. There is no way to fill this in that does not need a shell.
            Needs = new[]
            {
                new TemplateNeed("build or test anything", new[] { "run_command", "run_powershell" })
            }
        },

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
                Deny: [.. ShellTools.All]),
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
            //
            // git is NOT denied, and that was a defect on the day this shipped. The goal's own
            // default scope is "everything that has changed since the last commit", so the template
            // asked the model to review a diff and then forbade the only tool that produces one. A
            // deny that contradicts the template's own goal is not caution, it is a template that
            // cannot do its job - and since run_command is allowed here, denying git never stopped
            // anything anyway. What keeps this review from committing is that it is a review: the
            // goal says to change nothing except the report, and the reviewer checks that.
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "edit_file", "move_file", "docker" }),
            // No criteria, deliberately. A review's verdict IS an opinion, and there is no exit code
            // for one - so the run report will say "nothing verified this run", which is true.
            Limits: new ExecutionLimits(MaxSteps: 8),
            ReviewRequired: true,
            Builtin: true)
        {
            // THE case the defect log was written about. The goal never says "git" - it says
            // "everything that has changed since the last commit", which needs a diff without
            // naming one, which is why the test that checks a template's own WORDS could never have
            // caught this. Written down here because only a person can read the meaning.
            //
            // Conditional, and the escape says so: point the scope at a folder and no diff is
            // needed. Refusing a schedule that would have worked is the same failure as accepting
            // one that would not.
            Needs = new[]
            {
                new TemplateNeed(
                    "see what has changed",
                    new[] { "git", "run_command", "run_powershell" },
                    Otherwise: "or set 'What to review' to a scope that is not a diff",
                    // Only while the scope is still the default one, which IS a diff. Somebody who
                    // has already pointed this at a folder needs no diff and must not be refused
                    // for being unable to produce one.
                    WhenParameterIsDefault: "scope")
            }
        },

        new TaskTemplate(
            Id: "improve-tests",
            Name: "Improve Test Coverage",
            // The goal, the parameter and the criterion below are the ones that survived a day of
            // running this template against two real projects. Each sentence
            // that looks like over-explanation is there because a run went wrong without it.
            Goal: "Add tests to this workspace for {area}.\n\n"
                + "Write tests for behaviour that is not covered yet, in the style of the tests that "
                + "are already here. Each test must FAIL if the behaviour it describes is broken — "
                + "check that by breaking it temporarily and putting it back. Do not change any "
                + "source file to make a test pass.\n\n"
                + "Test command: {test_command}\n\n"
                + "Run the tests with THAT command and no other. If it exits 0 and prints nothing at "
                + "all it ran no tests — say so and find the command that does, rather than reporting "
                + "a pass.\n\n"
                + "EVERY time you run the test command — at any step, including immediately after "
                + "writing a new test — pass \"expectedExitCodes\": [0, 1] to run_command. This task "
                + "writes tests for behaviour that is not covered and may well be broken, so a "
                + "non-zero exit from the TEST COMMAND is expected at every step: it is the finding, "
                + "not a malfunction. Report which tests failed and why. Do NOT pass expectedExitCodes "
                + "to a build or a restore — those are meant to succeed, and a non-zero exit there is "
                + "a real failure to fix.",
            Version: 3,
            Description: "Add tests for what is not covered, and prove they can fail.",
            Category: "Development",
            Parameters: new[]
            {
                new TemplateParameter("area", "What to cover", TemplateParameterType.Text,
                    Description: "A file, a class, or an area of behaviour.",
                    Default: "the behaviour with the least coverage you can identify from the tests already here"),
                // Deliberately no default. 'dotnet test' on a console-app test project and
                // 'dotnet run --project' on an xunit project both exit 0 having run nothing, and a
                // green result that means nothing is worse than a form field that has to be filled.
                new TemplateParameter("test_command", "Test command", TemplateParameterType.Text,
                    Description: "How THIS project runs its tests. 'dotnet test' for a normal test "
                        + "project; 'dotnet run --project <path>.csproj' where the tests are a console "
                        + "app of their own. No default: projects differ, and the wrong command exits "
                        + "0 having run nothing.")
            },
            Permissions: new PermissionCeiling(
                MaxLevel: PermissionLevel.Execute,
                Deny: new[] { "git", "docker" }),
            // No criteria of its own (version 3). It declared "Builds: dotnet build" - .NET's command in a template
            // for any project, which a Python or a JavaScript workspace fails - to ask whether the run left
            // compilable code; the engine asks that itself now, for every kind of project it knows ("No new build
            // errors", BuildRegression). And a template's own criteria LOCK the review of the final checks, while
            // this goal names its test command: the review kept trying to add it, and on 2026-10-08 that asked a
            // person three times and once ended a run before its first step.
            SuccessCriteria: [],
            Limits: new ExecutionLimits(MaxSteps: 10),
            ReviewRequired: true,
            Builtin: true)
        {
            // Unconditional, and the goal spends three paragraphs on HOW to run the test command.
            // A run that cannot run it can still write test files, which is the worst shape this
            // template has: tests nobody has ever seen fail, delivered as though they had.
            Needs = new[]
            {
                new TemplateNeed("run the tests it writes", new[] { "run_command", "run_powershell" })
            }
        },

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
        {
            // Unconditional: the first instruction in the goal is to run a command and report what
            // it returned.
            Needs = new[]
            {
                new TemplateNeed(
                    "find out what is outdated, or build after changing it",
                    new[] { "run_command", "run_powershell" })
            }
        }
    };
}
