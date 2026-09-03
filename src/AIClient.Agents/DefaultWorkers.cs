namespace AIClient.Agents;

using AIClient.Core.Permissions;
using AIClient.Core.Providers;
using AIClient.Core.Workers;

/// <summary>
/// The built-in worker roles (PLAN_v2 §2.5 — a Worker is a role, not a model picker). Each role carries
/// its own instructions, tool allowlist and default permission level; the orchestrator offers only the
/// role's allowed tools to the model and rejects anything outside the list. All roles share the same
/// configured model here (their ModelPolicy differs later); shared honesty rules and any global
/// instructions are appended to every role's system prompt.
/// </summary>
public static class DefaultWorkers
{
    public const string DefaultId = "developer";

    // Applied to every role — the failure mode we are guarding against is a small model inventing
    // command output or a "result" it never actually obtained, and hiding a failed command.
    private const string HonestyRules =
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
        + "(the exact text the tool returned under 'command output'), NEVER the command line itself.\n"
        + "- After writing a file that should hold real data, read it back to confirm it contains the real output.";

    public static IReadOnlyList<Worker> Build(ModelRef coder, string? globalInstructions = null)
    {
        string With(string baseInstructions)
        {
            var full = baseInstructions + HonestyRules;
            return string.IsNullOrWhiteSpace(globalInstructions)
                ? full
                : full + "\n\n## Global instructions (apply to every run)\n" + globalInstructions;
        }

        ModelPolicy Policy() => new(coder);

        return new[]
        {
            new Worker(
                "developer", "Developer",
                With("You are a developer agent working inside the user's workspace. You can create and "
                    + "edit files, read files, list directories, run shell commands (run_command) and run "
                    + "PowerShell (run_powershell — prefer it on Windows for WMI/CIM, Get-PSDrive, pipes), plus git and "
                    + "docker tools for version control and containers. Use the "
                    + "tools to accomplish the request, then reply with a short confirmation of what you actually did."),
                new[] { "write_file", "read_file", "list_dir", "run_command", "run_powershell", "git", "docker" },
                PermissionLevel.Execute, Policy()),

            new Worker(
                "reviewer", "Reviewer",
                With("You are a code reviewer and analyst. Investigate the workspace and explain findings. "
                    + "You may ONLY read files and list directories — you must not modify anything or run "
                    + "commands. Report issues, risks and suggestions clearly."),
                new[] { "read_file", "list_dir" },
                PermissionLevel.Observe, Policy()),

            new Worker(
                "ops", "Ops",
                With("You are a DevOps/operations agent. Use the dedicated git and docker tools for version "
                    + "control and containers; run_command/run_powershell for other shell (prefer run_powershell "
                    + "on Windows for system/WMI queries); read files and list directories for context. Avoid "
                    + "editing source files unless explicitly asked. Prefer safe, read-only commands first."),
                new[] { "read_file", "list_dir", "run_command", "run_powershell", "git", "docker" },
                PermissionLevel.Execute, Policy()),

            new Worker(
                "writer", "Writer",
                With("You are a technical writer. Create and edit documentation and text files, reading "
                    + "existing files for context. Do not run shell commands."),
                new[] { "write_file", "read_file", "list_dir" },
                PermissionLevel.Execute, Policy()),
        };
    }
}
