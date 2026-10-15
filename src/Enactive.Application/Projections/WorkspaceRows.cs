namespace Enactive.Application.Projections;

using Enactive.Workspace;

/// <summary>Which of the switcher's rows this is: the one you are in, another one, or one whose folder is gone.</summary>
internal enum WorkspacePlace { Current, Other, Missing }

/// <summary>
/// A workspace's row in the switcher, as the window and the service's Runs screen both draw it. Said once here: each
/// had its own copy, and two copies of "a missing folder outranks being current" are two places to stop agreeing.
/// </summary>
internal static class WorkspaceRows
{
    /// <summary>A missing folder outranks being current: the name is already red, and the edge agrees.</summary>
    public static WorkspacePlace Place(bool exists, bool isCurrent)
        => !exists ? WorkspacePlace.Missing : isCurrent ? WorkspacePlace.Current : WorkspacePlace.Other;

    /// <summary>
    /// The row's second line: the folder, or that it is gone. A folder that has gone is MARKED, not hidden - hiding it
    /// quietly erased a project from the list the first time a drive was not mounted.
    /// </summary>
    public static string Detail(WorkspaceEntry entry, bool exists)
        => exists ? entry.RootPath : "Folder not found · " + entry.RootPath;

    // Renaming and forgetting a workspace, in the window's words - which the service's switcher offers too, so said once.

    /// <summary>What the pencil on a row says.</summary>
    public const string RenameTip = "Rename. What Enactive calls it — the folder is not touched.";

    /// <summary>The rename prompt's title and its line under it.</summary>
    public const string RenameTitle = "Name this workspace";
    public const string RenamePrompt = "What Enactive calls it. The folder is not touched.";

    /// <summary>What the cross on a row says.</summary>
    public const string ForgetTip = "Forget this workspace. The folder and its history are left where they are.";

    /// <summary>
    /// Asked before a workspace is forgotten. Forgetting used to be harmless - the entry was a path and nothing else; it
    /// now holds the name given it and how a run behaves in it, and neither comes back with the folder - so it asks, and
    /// says what is actually lost.
    /// </summary>
    public static Question Forget(string name)
        => new($"Forget \u201c{name}\u201d?",
            "The folder and everything in it, including its run history, are left exactly where they are. "
            + "What goes is the name you gave it here and how runs are set up in it.",
            "Forget", "Keep");
}
