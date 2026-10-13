namespace Enactive.Server.Runs;

using Enactive.Workspace;

/// <summary>
/// The workspaces the browser may see and act in: the ones the service knows (its registry, <c>workspaces.json</c>) AND
/// under its roots (<see cref="WorkspaceRoots"/>, ADR 0002: the browser's workspaces are limited to the configured roots).
/// Every lookup of a workspace from a page goes through here - reading its history, files, timeline and inbox, probing
/// its machine, deleting, saving its settings, writing its templates, applying its proposals.
///
/// <para>Known alone was not enough. Only starting a run checked the roots, and the registry holds more than the folders
/// added from the browser: it is the service account's file, which a desktop under the same account writes too, and its
/// entries stay when server.json's roots are narrowed. A Viewer could then read files and probe a folder outside the
/// roots, and an Operator delete in it, because the folder had once been opened somewhere.</para>
/// </summary>
internal sealed class KnownWorkspaces(ServerEngine engine, WorkspaceRoots roots)
{
    /// <summary>The roots the workspaces are limited to - where a new one may be added from.</summary>
    public WorkspaceRoots Roots => roots;

    /// <summary>The known workspaces under the roots, in the registry's order (the one opened last first).</summary>
    public IReadOnlyList<WorkspaceEntry> All => engine.Workspaces.Entries.Where(Within).ToList();

    /// <summary>The known workspace at <paramref name="root"/> when it is under the roots; null otherwise.</summary>
    public WorkspaceEntry? Find(string root)
        => engine.Workspaces.Find(root) is { } entry && Within(entry) ? entry : null;

    private bool Within(WorkspaceEntry entry)
    {
        // The roots' own check, the one a run's start makes - not a second copy of it here. An entry whose path cannot be
        // read now (a drive not mounted, a path no longer valid) is one the browser cannot use: refused, never a page down.
        try
        {
            return roots.Refusal(entry.RootPath) is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
