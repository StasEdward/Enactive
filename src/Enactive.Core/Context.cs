namespace Enactive.Core.Context;

using Enactive.Core.Memory;

/// <summary>A workspace: a context of work with a real root on disk.</summary>
public sealed record WorkspaceInfo(Guid Id, string Name, string RootPath)
{
    /// <summary>
    /// A workspace identified by its folder, with an id that does not change when the folder does.
    ///
    /// <para>The id is written into every run, memory entry and inbox item, so it has to be the same
    /// id the next time this project is opened. It used to be a hash of the PATH, which meant
    /// renaming or moving the folder silently detached everything that had ever been recorded about
    /// it: the rows are still in the database, and per-workspace filtering matches none of them.
    /// Nothing reports this, because from the engine's side a workspace with no history and a
    /// workspace whose history is filed under another id look exactly alike.</para>
    ///
    /// <para>So the id is READ from <c>.enactive/workspace.json</c> when it is there, and derived
    /// from the path when it is not. This method never writes: pointing at a folder should not put a
    /// file in it. <see cref="Adopt"/> is the one that writes, and hosts call it where a workspace is
    /// actually taken up rather than merely named.</para>
    /// </summary>
    public static WorkspaceInfo For(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
        return new WorkspaceInfo(WorkspaceIdentity.Read(full) ?? IdFor(full), NameOf(full), full);
    }

    /// <summary>
    /// The same workspace, with its id written down beside it if it was not already.
    ///
    /// <para><b>The id written is the one the workspace ALREADY HAD</b> — the path hash, for a
    /// workspace that has been used before this existed. That is the whole reason this is safe to
    /// ship: recording the existing id detaches nothing, and from that moment the folder can be
    /// renamed or moved without losing what it has done. Minting a fresh id here would inflict, on
    /// every existing workspace at once, exactly the defect being fixed.</para>
    ///
    /// <para>Best-effort. A folder that cannot be written to still opens, on the id it has always
    /// had; a workspace on a read-only share is a workspace, and refusing to show its history
    /// because a marker file could not be created would be a worse answer than the one it replaces.
    /// </para>
    /// </summary>
    public static WorkspaceInfo Adopt(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
        var id = WorkspaceIdentity.Read(full);
        if (id is null)
        {
            id = IdFor(full);
            WorkspaceIdentity.Write(full, id.Value);
        }

        return new WorkspaceInfo(id.Value, NameOf(full), full);
    }

    private static string NameOf(string fullPath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        return string.IsNullOrEmpty(name) ? "workspace" : name;
    }

    /// <summary>
    /// The id a folder has until one is written down for it: the first 16 bytes of SHA-256 over its
    /// normalised path. Case is folded where the filesystem folds it, so C:\Work\Proj and
    /// c:\work\proj are one workspace on Windows and two on Linux, which is what those filesystems
    /// actually mean.
    ///
    /// <para>Still here, and still used for two things. It is the seed <see cref="Adopt"/> writes,
    /// so nothing that already has a history loses it. And it is what AUTHORITY is keyed by — see
    /// the note on <see cref="WorkspaceIdentity"/>: a permission granted to a folder stays granted
    /// to that folder, and is not carried by a file the folder's own contents could supply.</para>
    /// </summary>
    public static Guid IdFor(string rootPath)
    {
        var normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            normalised = normalised.ToLowerInvariant();

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalised));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>What the user is focused on when an intent is raised (UI supplies this).</summary>
public sealed record IntentFocus(Guid? WorkspaceId, string? FilePath = null, string? Selection = null);

/// <summary>
/// Context assembled by the application, not typed by the user
/// ("user provides intent, application provides context"). PLAN_v2 §2.2.
/// </summary>
public sealed record WorkContext(
    Guid? WorkspaceId,
    string? ProjectName,
    string? FilePath,
    string? Selection,
    string? GitBranch,
    IReadOnlyList<string> RelatedFiles,
    IReadOnlyList<string> RecentChanges,
    EnvironmentInfo? Environment = null)
{
    public Enactive.Core.Tools.TaskActionPolicy? ActionPolicy { get; init; }
    public IReadOnlyList<Enactive.Core.Tools.TaskRestriction> Restrictions { get; init; } = [];
    /// <summary>
    /// What this project already knows: decisions the user made, and how earlier runs ended. Newest
    /// LAST, already bounded by whoever assembled the context.
    ///
    /// <para>PLAN_v2 §11 carried "project memory is written, never read back" as the gap that made
    /// the store a write-only log: the recorder folded decisions into it, the window rendered it,
    /// and the next run began knowing nothing about the last one. This is the field that was
    /// missing. An init property rather than a positional parameter so every existing construction
    /// of a context still compiles and still means what it meant.</para>
    /// </summary>
    public IReadOnlyList<MemoryEntry> Memory { get; init; } = Array.Empty<MemoryEntry>();

    /// <summary>
    /// How much of what there is, by folder — see <see cref="WorkspaceCensus"/>. Read by the
    /// planner, which otherwise sizes steps against a workspace it has never seen.
    ///
    /// <para>An init property for the same reason as <see cref="Memory"/>: every existing
    /// construction of a context still compiles and still means what it meant.</para>
    /// </summary>
    public IReadOnlyList<string> Inventory { get; init; } = Array.Empty<string>();
}

/// <summary>Builds <see cref="WorkContext"/> from the current focus.</summary>
public interface IContextProvider
{
    Task<WorkContext> BuildAsync(IntentFocus focus, CancellationToken ct);
}
