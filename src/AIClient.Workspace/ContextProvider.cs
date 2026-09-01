namespace AIClient.Workspace;

using AIClient.Core.Context;

/// <summary>
/// Assembles <see cref="WorkContext"/> for the current workspace. For the first slice it fills the
/// workspace identity and (if present) the current git branch; related files / recent changes grow later.
/// </summary>
public sealed class ContextProvider : IContextProvider
{
    private readonly WorkspaceInfo _workspace;

    public ContextProvider(WorkspaceInfo workspace) => _workspace = workspace;

    public Task<WorkContext> BuildAsync(IntentFocus focus, CancellationToken ct)
    {
        var context = new WorkContext(
            WorkspaceId: _workspace.Id,
            ProjectName: _workspace.Name,
            FilePath: focus.FilePath,
            Selection: focus.Selection,
            GitBranch: TryReadGitBranch(_workspace.RootPath),
            RelatedFiles: Array.Empty<string>(),
            RecentChanges: Array.Empty<string>());

        return Task.FromResult(context);
    }

    private static string? TryReadGitBranch(string root)
    {
        try
        {
            var head = Path.Combine(root, ".git", "HEAD");
            if (!File.Exists(head))
                return null;

            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
        }
        catch
        {
            return null;
        }
    }
}
