namespace Enactive.Workspace;

using Enactive.Core.Artifacts;

/// <summary>Creates git/filesystem snapshot sessions for the application hosts.</summary>
public sealed class WorkspaceChangesFactory : IWorkspaceChangesFactory
{
    public IWorkspaceChanges Create(string root) => new WorkspaceChanges(root);
}
