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
}
