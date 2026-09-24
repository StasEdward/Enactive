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
        + "cause. Never substitute a plausible-looking placeholder value.\n"
        // Measured 2026-09-24: one write_file turn took 72.1 s to GENERATE - 4,709 tokens, a report of
        // 18,000 characters - and 32 ms to write. The request asked for discrepancies; most of the
        // report listed the claims that matched, one entry each. What a model writes is the slowest
        // thing it does on any provider, and a line nobody asked for costs that time for nothing.
        // Stated for every kind of report - a disk check, a log analysis, a review - not for one task.
        + "- A report or written result holds what the request asked for. If it asks for problems - "
        + "discrepancies, failures, errors, risks - write those in full; what you checked and found in order "
        + "is ONE line with a count (\"41 other claims checked, all match\"), not an entry each, unless the "
        + "request asks for them. Writing is the slowest thing you do: every line costs time, and a line "
        + "nobody asked for costs it for nothing.";

    // Verifying a write by reading it back catches a weak local model's fabrication, but it costs an extra
    // round-trip that's wasteful on a strong model — so it's toggled via settings (VerifyWrites) rather than
    // baked into the always-on rules above.
    private const string ReadBackRule =
        "\n- After writing a file that should hold real data, read it back to confirm it contains the real output.";

    /// <summary>
    /// What to reach for when the answer is small.
    ///
    /// <para>Registering a tool is not offering it, and offering it is not making the case for it.
    /// A model reaches for what it knows, and what every model knows is <c>read_file</c>: measured
    /// 2026-09-12, a worker with read, search and list spent 114 calls and 88k prompt tokens on an
    /// audit and wrote nothing, because every question it had could only be answered by reading
    /// toward the answer. The sentence has to say WHEN to use these, not just that they exist -
    /// "a number, a list of files, or a yes/no" is the shape of question that must stop costing
    /// file content.</para>
    ///
    /// <para><b>One word changed, 2026-09-23, and the force kept.</b> An outside review called
    /// "do NOT read toward it" a rule with no edge, and gave the case that breaks it: <i>"how many
    /// services implement IHostedService, and which of them start a timer"</i> is answered by
    /// counting AND then reading, and a flat prohibition tells the model the second half is
    /// forbidden. True — but the measurement above is a worker that read toward every answer for
    /// 114 calls, and the same shape turned up again on 2026-09-23 in an audit that read for sixty
    /// turns and wrote nothing. So the sentence keeps its edge and gains the other half: ask first,
    /// read when you need what is IN a file.</para>
    ///
    /// <para>"A list of files" stays, against the review's advice, and is made precise instead. The
    /// question it means is "which files contain this", which <c>count_matches</c> answers without
    /// opening any of them — not "what is in this folder", which is <c>list_dir</c>'s.</para>
    /// </summary>
    private const string AggregateReads =
        "When what you need is a number, WHICH FILES contain something, or a yes/no, ask for it "
        + "rather than reading toward it: count_matches counts a pattern and names the files "
        + "holding it, file_stats gives sizes and line counts before you open anything, and "
        + "compare_files says whether two files are the same. Then read a file when you need what "
        + "is IN it. ";

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
            + "reading files one by one to look for something), read SEVERAL files at once "
            + "(read_files - one turn instead of five when a question touches more than one "
            + "file), list directories, create folders, move "
            + "files and copy them (copy_file — use it rather than reading a file and writing it back, "
            + "which truncates anything large and produces a partial copy that looks whole), delete a "
            + "file (delete_file — it always asks first, so use it only when removal is what was "
            + "actually requested), run shell "
            + "commands (run_command) and run "
            + "PowerShell (run_powershell — prefer it on Windows for WMI/CIM, Get-PSDrive, pipes), plus git and "
            + "docker tools for version control and containers. Use the "
            + AggregateReads
            + "Use the "
            + "tools to accomplish the request, then reply with a short confirmation of what you actually did.",
            new[] { "write_file", "edit_file", "read_file", "read_files", "search_files",
                    "count_matches", "file_stats", "compare_files", "list_dir", "create_directory",
                    "move_file", "copy_file", "delete_file", "run_command", "run_powershell", "git", "docker",
                    // Offered only where an SMTP account is configured - the tool is not
                    // registered otherwise, so naming it here costs nothing until somebody
                    // fills the section in.
                    "send_email" },
            PermissionLevel.Execute),

        ("reviewer", "Reviewer",
            "You are a code reviewer and analyst. Investigate the workspace and explain findings. "
            + "Use search_files to locate things by content instead of reading files one by one. "
            + AggregateReads
            + "Use only the read-only tools you have been given - you must not modify anything "
            + "or run commands. Report issues, risks and suggestions clearly.",
            new[] { "read_file", "read_files", "search_files", "count_matches", "file_stats", "compare_files",
                    "list_dir" },
            PermissionLevel.Observe),

        ("ops", "Ops",
            "You are a DevOps/operations agent. Use the dedicated git and docker tools for version "
            + "control and containers; run_command/run_powershell for other shell (prefer run_powershell "
            + "on Windows for system/WMI queries); read files, search them by content (search_files) and "
            + "list directories for context. " + AggregateReads + "Look before you change "
            + "anything: prefer safe, read-only commands first. You have no file-editing tool, and "
            + "writing source files THROUGH a shell is not a way around that - if a request needs "
            + "source changed, say so rather than doing it with Set-Content.",
            new[] { "read_file", "read_files", "search_files", "count_matches", "file_stats", "compare_files",
                    "list_dir", "run_command", "run_powershell", "git", "docker",
                    "send_email" },
            PermissionLevel.Execute),

        ("writer", "Writer",
            "You are a technical writer. Create and edit documentation and text files, reading "
            + "existing files for context and using search_files to find where something is written. "
            + AggregateReads
            + "You may also create folders, move files and copy them (copy_file - never read a file and write it back to copy it, which truncates anything large). Do not run shell commands.",
            new[] { "write_file", "edit_file", "read_file", "read_files", "search_files",
                    "count_matches", "file_stats", "compare_files", "list_dir",
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
