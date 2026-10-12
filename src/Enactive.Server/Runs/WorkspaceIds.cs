namespace Enactive.Server.Runs;

using Enactive.Core.Context;
using Enactive.Workspace;

/// <summary>
/// A workspace in an address is its id, never its path: a Windows path (drive colon, backslashes, spaces) does not
/// survive a URL segment, and a path taken from an address would let a caller name any folder on the machine. The id is
/// what the folder carries (<see cref="WorkspaceInfo.For"/>), and it is looked up only among the workspaces the service
/// already knows.
/// </summary>
internal sealed class WorkspaceIds(ServerEngine engine)
{
    /// <summary>The id a folder carries. Read only - <c>Adopt</c> would write an id file into somebody's project.</summary>
    public Guid IdOf(string root) => WorkspaceInfo.For(root).Id;

    /// <summary>The known workspace whose folder carries <paramref name="id"/>, or null when no known one does.</summary>
    public WorkspaceEntry? Find(Guid id)
    {
        foreach (var entry in engine.Workspaces.Entries)
        {
            // One known workspace whose drive is not mounted, or whose path is no longer valid, must not take the page
            // down for every other workspace: the registry keeps such entries (it does not assume the user threw them
            // away), so the scan has to step over them.
            try
            {
                if (WorkspaceInfo.For(entry.RootPath).Id == id)
                    return entry;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        return null;
    }
}
