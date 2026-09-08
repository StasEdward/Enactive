namespace Enactive.Core.Context;

using Enactive.Core.Memory;

/// <summary>A workspace: a context of work with a real root on disk.</summary>
public sealed record WorkspaceInfo(Guid Id, string Name, string RootPath)
{
    /// <summary>
    /// A workspace identified by its folder, with an id DERIVED from that folder rather than freshly
    /// generated. That id is written into every run, memory entry and inbox item, so it has to be the
    /// same id the next time the same folder is opened: a random one makes the field meaningless, and
    /// a shared database then cannot tell one project's rows from another's - per-workspace filtering
    /// matches nothing at all, silently.
    /// </summary>
    public static WorkspaceInfo For(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        return new WorkspaceInfo(IdFor(full), string.IsNullOrEmpty(name) ? "workspace" : name, full);
    }

    /// <summary>
    /// The stable id of a folder: the first 16 bytes of SHA-256 over its normalised path. Case is
    /// folded where the filesystem folds it, so C:\Work\Proj and c:\work\proj are one workspace on
    /// Windows and two on Linux, which is what those filesystems actually mean.
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
}

/// <summary>Builds <see cref="WorkContext"/> from the current focus.</summary>
public interface IContextProvider
{
    Task<WorkContext> BuildAsync(IntentFocus focus, CancellationToken ct);
}
