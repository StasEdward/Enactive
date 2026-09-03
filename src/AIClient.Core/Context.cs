namespace AIClient.Core.Context;

/// <summary>A workspace: a context of work with a real root on disk.</summary>
public sealed record WorkspaceInfo(Guid Id, string Name, string RootPath);

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
    EnvironmentInfo? Environment = null);

/// <summary>Builds <see cref="WorkContext"/> from the current focus.</summary>
public interface IContextProvider
{
    Task<WorkContext> BuildAsync(IntentFocus focus, CancellationToken ct);
}
