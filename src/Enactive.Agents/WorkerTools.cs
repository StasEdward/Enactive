namespace Enactive.Agents;

/// <summary>
/// Tools a worker already has the capability for, whether or not its saved list names them.
///
/// <para><b>Why this exists.</b> A role's tool list is SAVED. Adding a tool to
/// <see cref="DefaultWorkers"/> reaches a new installation and nobody else - and "nobody else" is
/// every installation that has ever been opened. <c>edit_file</c> shipped that way and was dead for
/// everyone until a migration handed it out; <c>search_files</c>, <c>create_directory</c> and
/// <c>move_file</c> then shipped the same way and stayed dead longer, which is how a task to rename
/// one file reached a shell instead: the model had no tool that could move anything, so the only
/// route was a command line.</para>
///
/// <para><b>Why it is safe.</b> Every entry below is a capability the worker ALREADY has by another
/// name, so this can never widen anyone's access:</para>
/// <list type="bullet">
/// <item><c>write_file</c> → <c>edit_file</c>: changing part of a file is narrower than replacing
/// all of it.</item>
/// <item><c>write_file</c> → <c>create_directory</c>: writing <c>a/b/c.txt</c> already creates
/// <c>a/b</c>. This only lets it be done on purpose.</item>
/// <item><c>write_file</c> → <c>move_file</c>: whoever may overwrite a file's contents may rename
/// it, and a rename is the lesser act. It also REPLACES the way this was being done - a shell -
/// which is unjournalled and therefore cannot be reverted when a reviewer rejects the step.</item>
/// <item><c>read_file</c> → <c>search_files</c>: finding a string is a faster way to do what
/// reading files one at a time already does.</item>
/// <item><c>read_file</c> → <c>count_matches</c>, <c>file_stats</c>, <c>compare_files</c>: each
/// returns LESS than reading would — a number, a size, a verdict — about files the worker may
/// already open one by one. They are the cheap way to ask a question whose answer is small, and
/// the reason they belong here rather than in a screen somebody has to find is that a worker which
/// cannot count has to read, which is what fills a context window with material nobody uses.</item>
/// <item><c>read_file</c> AND <c>write_file</c> → <c>copy_file</c>: a worker that can read a file
/// and write another can already make a copy by hand. It just cannot make a WHOLE one - reading
/// stops at 8000 characters, so the copy comes out partial and looks complete. Both are required:
/// with write_file alone, copy_file would let a worker duplicate content it is not allowed to
/// read, which is the one way this table could widen access rather than name it.</item>
/// </list>
///
/// <para>Deliberately NOT here: anything that reaches outside the workspace or hands a command line
/// to the operating system. A shell is not implied by anything, and never will be.</para>
///
/// <para><b>And deliberately not <c>delete_file</c>.</b> It is tempting - it shipped at the same
/// time as <c>copy_file</c> and will otherwise reach only new installations, which is the exact
/// complaint this class exists to answer. But it is not a capability anybody already has under
/// another name: overwriting a file destroys its contents and leaves the path, and no combination
/// of the existing tools removes one. Granting it here would be handing every saved role a power it
/// never had, in a migration, silently - which is a worse failure than the one being fixed. It is
/// in the built-in roles, and somebody who wants it in a role they already have can add it in
/// Settings, having decided to.</para>
/// </summary>
public static class WorkerTools
{
    /// <summary>
    /// (what the worker must ALREADY have, what then follows from having it).
    ///
    /// <para>A set rather than a single tool, because <c>copy_file</c> needs both halves: it is the
    /// one entry where taking the requirement loosely would grant something new instead of naming
    /// something already held.</para>
    /// </summary>
    private static readonly (string[] Requires, string Implies)[] Implications =
    [
        (["write_file"], "edit_file"),
        (["write_file"], "create_directory"),
        (["write_file"], "move_file"),
        (["write_file", "read_file"], "copy_file"),
        (["read_file"], "search_files"),
        (["read_file"], "count_matches"),
        (["read_file"], "file_stats"),
        (["read_file"], "compare_files")
    ];

    /// <summary>
    /// The wildcards, which are not tools and so are in no registry. <c>*</c> has to be tickable on
    /// purpose because an empty list means "no tools", and <c>mcp__*</c> covers whatever an MCP
    /// server turns out to expose.
    /// </summary>
    private static readonly string[] Wildcards = ["mcp__*", "*"];

    /// <summary>
    /// Every tool the settings editor should offer: the ones the host actually registered, the
    /// wildcards, and anything this worker already carries that is in neither list.
    ///
    /// <para><b>Why this is not a literal in the dialog.</b> It was one. The editor held its own
    /// hand-written array of tool names, and a tool absent from it could not be ticked - so
    /// <c>create_directory</c>, <c>move_file</c>, <c>copy_file</c>, <c>search_files</c> and
    /// <c>delete_file</c> were registered by the host, offered to nobody, and grantable only by
    /// editing settings.json by hand. The ones visible in the dialog today are visible only because
    /// a MIGRATION put them in the worker's saved list first, and the last clause below then kept
    /// them through the round trip. <c>delete_file</c>, which no migration hands out on purpose,
    /// was unreachable entirely: shipped, registered, tested, and impossible to switch on.</para>
    ///
    /// <para>Reading the registry means the next tool appears the day it is registered, with nobody
    /// remembering a second list. That is the same argument as <see cref="WithImplied"/>: the rule
    /// is written once, where it can be tested, instead of copied to wherever it is needed.</para>
    /// </summary>
    /// <param name="registered">Tool names the host registered - <c>IToolRegistry.Definitions</c>.</param>
    /// <param name="held">What this worker's saved list already names.</param>
    public static IReadOnlyList<string> Offerable(
        IEnumerable<string> registered, IEnumerable<string> held)
    {
        var result = new List<string>();

        void Add(string name)
        {
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        }

        foreach (var tool in registered)
            Add(tool);

        foreach (var wildcard in Wildcards)
            Add(wildcard);

        // Last, and never dropped: a tool this worker carries that the host does not register is
        // still its tool. Silently losing it on the round trip through the dialog would disarm a
        // role because somebody opened it and pressed Save.
        foreach (var tool in held)
            Add(tool);

        return result;
    }

    /// <summary>
    /// The same list plus anything it implies, in a stable order: implied tools follow the tool
    /// they came from, so a list stays readable in the settings editor.
    ///
    /// <para>Idempotent, because a migration that is not can only be run once safely and nobody
    /// finds out which time was the second.</para>
    /// </summary>
    public static IReadOnlyList<string> WithImplied(IReadOnlyList<string> tools)
    {
        // "*" is every tool there is. Adding names to it would say less, not more.
        if (tools.Contains("*", StringComparer.OrdinalIgnoreCase))
        {
            return tools;
        }

        var result = tools.ToList();

        foreach (var (requires, implies) in Implications)
        {
            // EVERY requirement, not any of them.
            if (!requires.All(r => result.Contains(r, StringComparer.OrdinalIgnoreCase))
                || result.Contains(implies, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // After the first tool it follows from, and after anything already inserted there, so
            // the order reads write_file, edit_file, create_directory, move_file rather than
            // reversed. Cosmetic, and it is what the settings editor shows somebody.
            var at = result.FindIndex(t => string.Equals(t, requires[0], StringComparison.OrdinalIgnoreCase));
            var insert = at + 1;

            while (insert < result.Count
                   && Implications.Any(i =>
                       string.Equals(i.Implies, result[insert], StringComparison.OrdinalIgnoreCase)))
            {
                insert++;
            }

            result.Insert(insert, implies);
        }

        return result;
    }
}
