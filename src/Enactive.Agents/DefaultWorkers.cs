namespace Enactive.Agents;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Core.Workers;

/// <summary>
/// The built-in worker roles (a Worker is a role, not a model picker). Each role carries
/// its own base instructions, tool allowlist and default permission level. <see cref="Seed"/> returns the
/// roles with their BASE instructions (used to seed the editable team in settings); <see cref="Build"/>
/// additionally appends the shared honesty rules and any global instructions to every role — that
/// augmentation is a runtime concern (<see cref="Augment"/>), not something persisted per worker.
/// </summary>
public static class DefaultWorkers
{
    public const string DefaultId = "developer";

    // Common evidence/reporting contract. Operational guidance is added only for relevant tools.
    public const string HonestyRules =
        "\n\nImportant rules:\n"
        + "- Never invent command output, file contents, numbers or results. Report observed evidence; "
        + "state uncertainty and real errors instead of substituting placeholder values.\n"
        + "- A report or written result holds what the request asked for. When asked for problems, "
        + "describe them; passing findings are ONE line with a count unless requested individually.";

    private const string BatchReads =
        "\n- Reads and searches that do not depend on each other go TOGETHER: read_file accepts "
        + "'paths'; search_files includes surrounding lines; one reply can make several independent calls.";

    private const string ScratchRules =
        "\n- Put helper scripts, temporary copies and long command logs in '" + WorkspaceGuard.ScratchPrefix
        + "/'. Scratch files are excluded from review, workspace changes and rollback. "
        + "Keep deliverables in the workspace, not in scratch.";

    private const string CommandRules =
        "\n- Read command results before describing them. If output is truncated, capture the full log "
        + "in scratch and inspect the relevant parts; never present a truncated excerpt as a complete log. "
        + "Report and resolve unexpected failures; an expected test failure is evidence, not a reason to hide it."
        // Run bb77e810, 2026-10-09: asked why tests failed, a worker traced the code by hand for two replies - 5,532 and
        // 8,192 tokens of reasoning, seven of the step's thirteen minutes - laying out the same inputs again and again
        // and reaching a different answer each time, without running one test. A run answers that in seconds.
        + "\n- When you need to know what code or a command DOES with given inputs, run it - the existing test that "
        + "covers it, the program itself, or a small script in scratch - rather than working it out in your head. "
        + "A trace done by hand goes wrong easily and takes longer than the run.";

    private const string SaveOutputRules =
        "\n- To save requested command output, write_file the exact result only if it is complete. "
        + "Otherwise capture the full output in scratch and copy_file it into place. Save the output, "
        + "never the command line or a shortened preview.";

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
            + "actually requested), put a file back exactly as it was before the run (restore_file - to undo a change "
            + "made on purpose, rather than editing it back by hand), run shell "
            + "commands (run_command) and run "
            + "PowerShell (run_powershell — prefer it on Windows for WMI/CIM, Get-PSDrive, pipes), plus git and "
            + "docker tools for version control and containers. Use the "
            + AggregateReads
            + "Use the "
            + "tools to accomplish the request, then reply with a short confirmation of what you actually did.",
            new[] { "write_file", "edit_file", "read_file", "read_files", "search_files",
                    "count_matches", "file_stats", "compare_files", "list_dir", "create_directory",
                    "move_file", "copy_file", "delete_file", "restore_file", "run_command", "run_powershell", "git", "docker",
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
            + "You may also create folders, move files and copy them (copy_file - never read a file and write it back to copy it, which truncates anything large), "
            + "and put a file back as it was before the run (restore_file). Do not run shell commands.",
            new[] { "write_file", "edit_file", "read_file", "read_files", "search_files",
                    "count_matches", "file_stats", "compare_files", "list_dir",
                    "create_directory", "move_file", "copy_file", "restore_file" },
            PermissionLevel.Execute),
    };

    /// <summary>
    /// Adds evidence rules and guidance for the worker's built-in tools, without altering saved instructions.
    /// A null allowlist retains the legacy all-tools augmentation for existing callers.
    /// Tool names here select API-specific advice only; they do not grant permissions or classify effects.
    /// </summary>
    public static string Augment(string baseInstructions, string? globalInstructions = null,
        bool verifyWrites = true, IReadOnlyList<string>? tools = null)
    {
        // The role's tools as the gate reads them (ToolAllowlist), and nothing more. This used to add the
        // tools a list implies (WorkerTools.WithImplied), which saved lists are given once, by migration -
        // so a role somebody later took copy_file from was still advised to copy_file, and refused it.
        bool Has(string name) => tools is null || ToolAllowlist.Allows(tools, name);
        var reads = Has("read_file") || Has("read_files");
        var writes = Has("write_file") || Has("edit_file");
        var commands = Has("run_command") || Has("run_powershell") || Has("git") || Has("docker");

        var full = baseInstructions + HonestyRules;
        if (Has("read_file")) full += BatchReads;
        if (writes || commands) full += ScratchRules;
        if (commands) full += CommandRules;
        if (Has("run_powershell"))
            full += "\n- On Windows, prefer run_powershell for WMI/CIM, objects and pipelines.";
        if (Has("run_command"))
            full += "\n- On Windows, run_command uses cmd.exe: redirect with '> path'; PowerShell cmdlets need run_powershell.";
        if (commands && Has("write_file") && Has("copy_file")) full += SaveOutputRules;
        if (verifyWrites && writes && reads) full += ReadBackRule;
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
            .Select(r => new Worker(r.Id, r.Role, Augment(r.Instructions, globalInstructions, verifyWrites, r.Tools), r.Tools, r.Level, policy))
            .ToArray();
    }
}
