namespace Enactive.Workspace;

using Enactive.Core.Context;

/// <summary>
/// Assembles <see cref="WorkContext"/> for the current workspace. For the first slice it fills the
/// workspace identity and (if present) the current git branch; related files / recent changes grow later.
/// </summary>
public sealed class ContextProvider : IContextProvider
{
    private readonly WorkspaceInfo _workspace;
    private readonly IEnvironmentProbe? _environment;

    public ContextProvider(WorkspaceInfo workspace, IEnvironmentProbe? environment = null)
    {
        _workspace = workspace;
        _environment = environment;
    }

    public async Task<WorkContext> BuildAsync(IntentFocus focus, CancellationToken ct)
    {
        EnvironmentInfo? env = null;
        if (_environment is not null)
        {
            try { env = await _environment.ProbeAsync(_workspace, ct); }
            catch { env = null; }  // environment awareness is best-effort, never fails a run
        }

        return new WorkContext(
            WorkspaceId: _workspace.Id,
            ProjectName: _workspace.Name,
            FilePath: focus.FilePath,
            Selection: focus.Selection,
            GitBranch: env is { Git.Count: > 0 } e ? e.Git[0].Branch : TryReadGitBranch(_workspace.RootPath),
            RelatedFiles: Array.Empty<string>(),
            RecentChanges: Array.Empty<string>(),
            Environment: env);
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
