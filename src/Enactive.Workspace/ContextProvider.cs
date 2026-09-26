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
            Memory = memory,

            // Best-effort like the two above, and for the same reason: a run must not fail because
            // the workspace could not be counted. WorkspaceCensus swallows its own errors, so this
            // is belt and braces.
            Inventory = await CensusAsync(ct)
        };
    }

    /// <summary>
    /// What the project contains, for the planner — asked of git where there is a repository, and
    /// of the filesystem where there is not.
    ///
    /// <para><b>Why git and not a walk.</b> Run against this repository, a walk reported
    /// <c>work — 417 .ps1, 210 .log</c> and four folders of <c>.dll</c> before it reached a single
    /// source file: scratch piles, publish output and restored packages are most of what sits on
    /// disk and none of what anybody means by "the project". The skip list can chase the ones we
    /// know (<c>bin</c>, <c>obj</c>, <c>node_modules</c>) and will always be one folder behind the
    /// next one somebody adds. The repository already answers this question exactly - a file is
    /// part of the project when it is tracked - so where there is a repository, it is asked.</para>
    ///
    /// <para>Best-effort in both halves: no git, a broken repository, a slow disk or a timeout all
    /// end with the planner being told nothing, which is where it was before any of this.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> CensusAsync(CancellationToken ct)
    {
        try
        {
            var tracked = await GitFilesAsync(_workspace.RootPath, ct);
            if (tracked.Count > 0)
                return WorkspaceCensus.Of(tracked);
        }
        catch { /* falls through to the walk */ }

        try { return WorkspaceCensus.Of(_workspace.RootPath); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Every file the repository tracks, or an empty list. Two seconds is generous for a command
    /// that reads an index; past that the planner is better served by the fallback than by waiting.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GitFilesAsync(string root, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            return Array.Empty<string>();

        var (exit, output) = await AutomaticGit.RunAsync(root, ["ls-files"], ct, timeoutMs: 2000);
        return exit == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
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
