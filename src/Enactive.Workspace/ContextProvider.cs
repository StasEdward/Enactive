namespace Enactive.Workspace;

using Enactive.Core.Context;
using Enactive.Core.Memory;

/// <summary>
/// Assembles <see cref="WorkContext"/> for the current workspace. For the first slice it fills the
/// workspace identity and (if present) the current git branch; related files / recent changes grow later.
/// </summary>
public sealed class ContextProvider : IContextProvider
{
    private readonly WorkspaceInfo _workspace;
    private readonly IEnvironmentProbe? _environment;
    private readonly IMemoryStore? _memory;

    /// <summary>
    /// How many memory entries reach a run. Bounded because they are read into the PROMPT, where
    /// they compete with the work itself for the context window: a workspace with two hundred runs
    /// behind it would otherwise spend its window on its own history. Newest wins - what the last
    /// few runs concluded is what a new one can act on, and the rest is in the timeline.
    /// </summary>
    public const int MemoryLimit = 20;

    public ContextProvider(
        WorkspaceInfo workspace,
        IEnvironmentProbe? environment = null,
        IMemoryStore? memory = null)
    {
        _workspace = workspace;
        _environment = environment;
        _memory = memory;
    }

    public async Task<WorkContext> BuildAsync(IntentFocus focus, CancellationToken ct)
    {
        EnvironmentInfo? env = null;
        if (_environment is not null)
        {
            try { env = await _environment.ProbeAsync(_workspace, ct); }
            catch { env = null; }  // environment awareness is best-effort, never fails a run
        }

        // Best-effort, like the environment probe above and for the same reason: a run must not
        // fail because the project could not remember something.
        IReadOnlyList<MemoryEntry> memory = Array.Empty<MemoryEntry>();
        if (_memory is not null)
        {
            try
            {
                var all = await _memory.LoadAllAsync(ct);
                memory = all
                    .OrderBy(e => e.At)
                    .TakeLast(MemoryLimit)
                    .ToArray();
            }
            catch { memory = Array.Empty<MemoryEntry>(); }
        }

        return new WorkContext(
            WorkspaceId: _workspace.Id,
            ProjectName: _workspace.Name,
            FilePath: focus.FilePath,
            Selection: focus.Selection,
            GitBranch: env is { Git.Count: > 0 } e ? e.Git[0].Branch : TryReadGitBranch(_workspace.RootPath),
            RelatedFiles: Array.Empty<string>(),
            RecentChanges: Array.Empty<string>(),
            Environment: env)
        {
            Memory = memory
        };
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
