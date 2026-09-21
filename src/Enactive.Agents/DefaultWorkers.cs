namespace Enactive.Agents;

using Enactive.Core.Context;
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
        // "do NOT fight cmd quoting" used to be here, and it was advice about a BUG in this
        // application rather than about shells: run_command passed the command with the C runtime's
        // escaping, which cmd does not speak, so every quote came out as \". That is fixed (see
        // RunCommandTool), and telling the model to avoid quotes would now be teaching it to work
        // around something that no longer happens. The reason to prefer PowerShell is objects and
        // pipes, which is a real reason.
        + "- On Windows, prefer the run_powershell tool for anything working with OBJECTS - WMI/CIM "
        + "queries, Get-PSDrive, pipes, Select-Object - and write the script plainly. Quotes are safe in "
        + "either tool: write the command exactly as you would type it.\n"
        // The working area comes BEFORE the two rules that use it. It was written after them, with
        // the second saying "the one exception is below" four bullets ahead of the exception and a
        // blunt "do NOT redirect" in between - a forward reference a model reading in order never
        // reaches.
        + "- You have a working area of your own at '" + WorkspaceGuard.ScratchPrefix + "/'. Put things there that "
        + "are FOR the job but are not the job: a helper script you want to run, a scratch copy, a command's output "
        + "that was too long to come back in the tool result. It is an ordinary folder - write_file, read_file and "
        + "the shell all reach it by that path - but nothing in it is part of your work: it is not shown to the "
        + "reviewer, not included in what you changed, and not undone if the step is rejected. The workspace itself "
        + "is for the deliverable. Do not leave working files there.\n"
        + "- READING a command's output: it comes back in the tool result, and that is where to read it. Do not "
        + "redirect with '>' or '| Out-File' merely to avoid reading it - output you sent to a file and did not "
        + "open is output you have not seen, and you may not describe it. But the result is CUT at a few thousand "
        + "characters and SAYS SO when it is: past that point redirecting is the only way to get the whole thing. "
        + "Send it to '" + WorkspaceGuard.ScratchPrefix + "/', then read_file it, or search_files with \"path\": \""
        + WorkspaceGuard.ScratchPrefix + "\" to find the error in a long build log without reading it a screenful "
        + "at a time. REDIRECT WITH THE SHELL YOU ARE IN: in run_command that is '> path' and nothing else - it "
        + "is cmd.exe, and Set-Content, Select-String, Out-File and every other cmdlet are PowerShell and will "
        + "fail instantly with 'is not recognized'. Use run_powershell if you want a cmdlet or a pipe.\n"
        // The old pair of rules here - "write that exact returned text with write_file" and "the
        // file must contain the OUTPUT" - produced the very thing this rule set exists to stop.
        // Followed literally on a 500 KB build log they write six thousand characters and the words
        // "… (truncated)" into a file, and call it the command's output. copy_file exists for
        // exactly this: it streams bytes and never decodes them, which is why it was added when
        // read-then-write was found to be truncating large files silently.
        + "- SAVING a command's output to a file somebody asked for: if the result came back WHOLE, write that "
        + "exact text with write_file. If the result says it was cut, do NOT write_file it - that produces a "
        + "fraction of the log in a file that looks complete. Redirect the command into '"
        + WorkspaceGuard.ScratchPrefix + "/' instead and copy_file it into place, which copies every byte. Either "
        + "way the file holds the command's OUTPUT, never the command line itself.\n"
        + "- If a command fails (non-zero exit code, or an error in its output), report the real error and fix the "
        + "cause. Never substitute a plausible-looking placeholder value.";

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
            + "reading files one by one to look for something), list directories, create folders, move "
            + "files and copy them (copy_file — use it rather than reading a file and writing it back, "
            + "which truncates anything large and produces a partial copy that looks whole), delete a "
            + "file (delete_file — it always asks first, so use it only when removal is what was "
            + "actually requested), run shell "
            + "commands (run_command) and run "
            + "PowerShell (run_powershell — prefer it on Windows for WMI/CIM, Get-PSDrive, pipes), plus git and "
            + "docker tools for version control and containers. Use the "
            + "tools to accomplish the request, then reply with a short confirmation of what you actually did.",
            new[] { "write_file", "edit_file", "read_file", "search_files", "list_dir", "create_directory",
                    "move_file", "copy_file", "delete_file", "run_command", "run_powershell", "git", "docker",
                    // Offered only where an SMTP account is configured - the tool is not
                    // registered otherwise, so naming it here costs nothing until somebody
                    // fills the section in.
                    "send_email" },
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
            new[] { "read_file", "search_files", "list_dir", "run_command", "run_powershell", "git", "docker",
                    "send_email" },
            PermissionLevel.Execute),

        ("writer", "Writer",
            "You are a technical writer. Create and edit documentation and text files, reading "
            + "existing files for context and using search_files to find where something is written. "
            + "You may also create folders, move files and copy them (copy_file - never read a file and write it back to copy it, which truncates anything large). Do not run shell commands.",
            new[] { "write_file", "edit_file", "read_file", "search_files", "list_dir",
                    "create_directory", "move_file", "copy_file" },
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
