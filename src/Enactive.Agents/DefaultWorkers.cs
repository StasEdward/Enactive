namespace Enactive.Agents;

using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Workers;

/// <summary>
/// The built-in worker roles (PLAN_v2 §2.5 — a Worker is a role, not a model picker). Each role carries
/// its own base instructions, tool allowlist and default permission level. <see cref="Seed"/> returns the
/// roles with their BASE instructions (used to seed the editable team in settings); <see cref="Build"/>
/// additionally appends the shared honesty rules and any global instructions to every role — that
/// augmentation is a runtime concern (<see cref="Augment"/>), not something persisted per worker.
/// </summary>
public static class DefaultWorkers
{
    public const string DefaultId = "developer";

    // Applied to every role at build time — the failure mode we guard against is a small model inventing
    // command output or a "result" it never actually obtained, and hiding a failed command.
    public const string HonestyRules =
        "\n\nImportant rules:\n"
        + "- Never invent or guess command output, file contents, numbers or results. Only state values you "
        + "actually obtained from a tool call during this run.\n"
        + "- On Windows, for anything using PowerShell (WMI/CIM queries, Get-PSDrive, pipes, quotes) use the "
        + "run_powershell tool and write the script plainly — do NOT fight cmd quoting with run_command, and do "
        + "NOT put '| Out-File' or '>' in the script; let the tool return the output to you.\n"
        + "- To capture a command's output into a file: RUN the command (its stdout/stderr is returned to you in "
        + "the tool result), then write that exact returned text to the file with write_file. Do NOT redirect with "
        + "'>' into a file and then describe the output from memory — redirected output is not visible to you.\n"
        + "- If a command fails (non-zero exit code, or an error in its output), report the real error and fix the "
        + "cause. Never substitute a plausible-looking placeholder value.\n"
        + "- When asked to put a command's result/report into a file, the file must contain the command's OUTPUT "
        + "(the exact text the tool returned under 'command output'), NEVER the command line itself.";

    // Verifying a write by reading it back catches a weak local model's fabrication, but it costs an extra
    // round-trip that's wasteful on a strong model — so it's toggled via settings (VerifyWrites) rather than
    // baked into the always-on rules above.
    private const string ReadBackRule =
        "\n- After writing a file that should hold real data, read it back to confirm it contains the real output.";

    // The role table, defined once. Instructions here are the BASE (pre-augmentation) text.
    //
    // An allowlist here is the ONLY thing that makes a registered tool reachable: the host registers
    // a tool, and if no role names it the model is never offered it and the tool may as well not
    // exist. edit_file shipped that way for a day and cost three destroyed files; search_files,
    // create_directory and move_file shipped that way for longer and were found by the test that now
    // asserts every shipped tool is named by somebody.
    //
    // The rule the lists follow, so a new tool has an obvious home:
    //   - anything read-only (Observe) goes to every role, reviewer included;
    //   - a role that may write_file may also create_directory and move_file, which are strictly
    //     within what writing and reading files already allow;
    //   - running things stays with developer and ops.
    private static readonly (string Id, string Role, string Instructions, string[] Tools, PermissionLevel Level)[] Roles =
    {
        ("developer", "Developer",
            "You are a developer agent working inside the user's workspace. You can create files "
            + "(write_file), change PART of an existing file (edit_file — prefer it: it does not make "
            + "you retype the rest), read files, FIND things by content (search_files — prefer it over "
            + "reading files one by one to look for something), list directories, create folders and move "
            + "files, run shell commands (run_command) and run "
            + "PowerShell (run_powershell — prefer it on Windows for WMI/CIM, Get-PSDrive, pipes), plus git and "
            + "docker tools for version control and containers. Use the "
            + "tools to accomplish the request, then reply with a short confirmation of what you actually did.",
            new[] { "write_file", "edit_file", "read_file", "search_files", "list_dir", "create_directory",
                    "move_file", "run_command", "run_powershell", "git", "docker" },
            PermissionLevel.Execute),

        ("reviewer", "Reviewer",
            "You are a code reviewer and analyst. Investigate the workspace and explain findings. "
            + "Use search_files to locate things by content instead of reading files one by one. "
            + "You may ONLY read, search and list — you must not modify anything or run "
            + "commands. Report issues, risks and suggestions clearly.",
            new[] { "read_file", "search_files", "list_dir" },
            PermissionLevel.Observe),

        ("ops", "Ops",
            "You are a DevOps/operations agent. Use the dedicated git and docker tools for version "
            + "control and containers; run_command/run_powershell for other shell (prefer run_powershell "
            + "on Windows for system/WMI queries); read files, search them by content (search_files) and "
            + "list directories for context. Avoid "
            + "editing source files unless explicitly asked. Prefer safe, read-only commands first.",
            new[] { "read_file", "search_files", "list_dir", "run_command", "run_powershell", "git", "docker" },
            PermissionLevel.Execute),

        ("writer", "Writer",
            "You are a technical writer. Create and edit documentation and text files, reading "
            + "existing files for context and using search_files to find where something is written. "
            + "You may also create folders and move files. Do not run shell commands.",
            new[] { "write_file", "edit_file", "read_file", "search_files", "list_dir",
                    "create_directory", "move_file" },
            PermissionLevel.Execute),
    };

    /// <summary>Appends the shared honesty rules and (optionally) global instructions to a role's base text.</summary>
    public static string Augment(string baseInstructions, string? globalInstructions = null, bool verifyWrites = true)
    {
        var full = baseInstructions + HonestyRules + (verifyWrites ? ReadBackRule : string.Empty);
        return string.IsNullOrWhiteSpace(globalInstructions)
            ? full
            : full + "\n\n## Global instructions (apply to every run)\n" + globalInstructions;
    }

    /// <summary>The roles with their BASE instructions and the given model — for seeding the editable team.</summary>
    public static IReadOnlyList<Worker> Seed(ModelRef model)
    {
        var policy = new ModelPolicy(model);
        return Roles
            .Select(r => new Worker(r.Id, r.Role, r.Instructions, r.Tools, r.Level, policy))
            .ToArray();
    }

    /// <summary>The roles with honesty + global instructions applied and the given model on every role.</summary>
    public static IReadOnlyList<Worker> Build(ModelRef coder, string? globalInstructions = null, bool verifyWrites = true)
    {
        var policy = new ModelPolicy(coder);
        return Roles
            .Select(r => new Worker(r.Id, r.Role, Augment(r.Instructions, globalInstructions, verifyWrites), r.Tools, r.Level, policy))
            .ToArray();
    }
}
