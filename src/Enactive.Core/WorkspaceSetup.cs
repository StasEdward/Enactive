namespace Enactive.Core.Context;

/// <summary>
/// What a workspace needs before anything is asked of it: a working area that is really there,
/// and a git repository that does not offer the engine's own state as somebody's next commit.
///
/// <para>Both are idempotent and neither can fail a run. This happens on the way to doing what
/// the person asked for, and it must never be the reason that does not happen.</para>
/// </summary>
public static class WorkspaceSetup
{
    /// <summary>Everything below, in one call. Safe to repeat, and repeated on purpose.</summary>
    public static void Prepare(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        ScratchArea.Ensure(workspaceRoot);
        Ignore(workspaceRoot);
    }

    /// <summary>The line written, and the one looked for.</summary>
    public const string Entry = WorkspaceGuard.ReservedFolder + "/";

    private const string Because = "# Enactive's own state for this workspace: run history, the "
                                 + "undo journal, approvals, and the agent's working area.";

    /// <summary>
    /// Adds <c>.enactive/</c> to the workspace's <c>.gitignore</c>, if it has one.
    ///
    /// <para><b>Only if it has one.</b> Creating a <c>.gitignore</c> in a repository that chose
    /// not to have one is a decision about that project, and not ours; a folder nobody ignores in
    /// a project with no ignore file is between them and their tooling. What is ours is that we
    /// put the folder there, so where the project already keeps such a list, our folder belongs
    /// on it.</para>
    ///
    /// <para><b>It only ever appends.</b> The file is somebody's, often with comments and an
    /// order that means something; rewriting or sorting it would be a change nobody asked for in
    /// a file that goes into their next diff. One entry, under one line saying what it is, at the
    /// end.</para>
    ///
    /// <para>Returns whether it wrote anything, for a test and for nothing else. Every failure is
    /// swallowed: a read-only checkout, a file held open, a repository on a share - none of them
    /// is a reason a person cannot run a task.</para>
    /// </summary>
    public static bool Ignore(string workspaceRoot)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(workspaceRoot), ".gitignore");
            if (!File.Exists(path))
                return false;

            var lines = File.ReadAllLines(path);
            if (lines.Any(Mentions))
                return false;

            var text = File.ReadAllText(path);
            var needsBreak = text.Length > 0 && !text.EndsWith('\n');

            File.AppendAllText(
                path,
                (needsBreak ? Environment.NewLine : "")
                + Environment.NewLine + Because + Environment.NewLine + Entry + Environment.NewLine);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this line already ignores the folder, in any of the spellings git accepts for it.
    ///
    /// <para><c>.enactive</c>, <c>.enactive/</c>, <c>/.enactive</c>, <c>/.enactive/</c> and any of
    /// them with a trailing comment or spaces all mean the same thing to git, and appending a
    /// second entry because the first was written differently is the kind of edit that makes
    /// people stop trusting a tool with their files. A NEGATION - <c>!.enactive</c> - is somebody
    /// saying the opposite on purpose, and is left alone rather than argued with.</para>
    /// </summary>
    private static bool Mentions(string line)
    {
        var text = line.Trim();

        if (text.Length == 0 || text.StartsWith('#'))
            return false;

        // A negation is somebody saying the opposite on purpose, about THIS folder. It counts as
        // already decided and is left alone rather than argued with - but only when it is about
        // this folder: `!important.txt` is a line about something else.
        if (text.StartsWith('!'))
            text = text[1..];

        return string.Equals(
            text.Split('#')[0].Trim().Trim('/'),
            WorkspaceGuard.ReservedFolder,
            StringComparison.OrdinalIgnoreCase);
    }
}
