namespace Enactive.App.Ui;

using Enactive.Application;
using Enactive.Core.Checks;
using Enactive.Workspace;

/// <summary>
/// The window's Checks lists (Plan Phase 3.5): the global one in Settings, a workspace's from its card. Both straight to the
/// files, as the window's templates are, over the store every run reads (CheckStore, with the ecosystems a run knows, so
/// the implicit build and test are the ones a run would get) - and, in a workspace, nothing changed while any run holds it
/// (WorkspaceLease: this window's runs, foreground or background, and any other program's).
/// </summary>
internal static class DesktopChecks
{
    /// <param name="workspaceRoot">The workspace open in the window, or null: the global list then shows the global checks only.</param>
    /// <param name="globalFolder">Where the global checks are; null, next to the window's settings (CheckStore.DefaultGlobalFolder). A test's own.</param>
    public static ChecksViewModel For(string? workspaceRoot, CheckScope scope, string? globalFolder = null)
    {
        var root = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot);
        var store = new CheckStore(root, globalFolder, KnownEcosystems.All);
        return new ChecksViewModel(store, scope, new StoreCheckChanges(store, root));
    }
}
