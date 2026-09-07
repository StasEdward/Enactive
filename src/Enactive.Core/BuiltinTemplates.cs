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
/// <para>These three exist because the plan's M6 asked for the ones a person would actually run,
/// and because a library with nothing in it cannot be tried at all.</para>
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
                Deny: new[] { "write_file", "edit_file", "move_file", "create_dir" }),
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
                Deny: new[] { "write_file", "edit_file", "move_file", "create_dir" }),
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
            Builtin: true)
    };
}
