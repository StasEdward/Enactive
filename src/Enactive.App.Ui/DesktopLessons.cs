namespace Enactive.App.Ui;

using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Workspace;

/// <summary>
/// The window's Lessons list (Plan Phase 5.2): a workspace's store and the machine's Global file beside the window's
/// settings, the same two a run started here reads (RunComposer).
/// </summary>
internal static class DesktopLessons
{
    /// <param name="globalFolder">Where the Global rules are; null, next to the window's settings. A test's own.</param>
    public static LessonsViewModel For(string workspaceRoot, string workspaceName, string? globalFolder = null)
        => new(new BookLessons(new RuleBook(RuleStoreFactory.Create(WorkspaceInfo.For(Path.GetFullPath(workspaceRoot))),
                JsonRuleStore.Global(globalFolder))),
            $"Lessons - {workspaceName}");
}
