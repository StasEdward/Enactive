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
/// </list>
///
/// <para>Deliberately NOT here: anything that reaches outside the workspace or hands a command line
/// to the operating system. A shell is not implied by anything, and never will be.</para>
/// </summary>
public static class WorkerTools
{
    /// <summary>(the tool a worker has, the tool that follows from having it).</summary>
    private static readonly (string Has, string Implies)[] Implications =
    [
        ("write_file", "edit_file"),
        ("write_file", "create_directory"),
        ("write_file", "move_file"),
        ("read_file", "search_files")
    ];

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

        foreach (var (has, implies) in Implications)
        {
            var at = result.FindIndex(t => string.Equals(t, has, StringComparison.OrdinalIgnoreCase));

            if (at < 0 || result.Contains(implies, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // After the tool it follows from, and after anything already inserted there, so the
            // order reads write_file, edit_file, create_directory, move_file rather than reversed.
            var insert = at + 1;
            while (insert < result.Count
                   && Implications.Any(i =>
                       string.Equals(i.Has, has, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(i.Implies, result[insert], StringComparison.OrdinalIgnoreCase)))
            {
                insert++;
            }

            result.Insert(insert, implies);
        }

        return result;
    }
}
