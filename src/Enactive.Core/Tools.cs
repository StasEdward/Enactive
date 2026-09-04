namespace Enactive.Core.Tools;

using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Permissions;

/// <summary>Describes a tool to the model (name + description + JSON Schema for arguments).</summary>
public sealed record ToolDefinition(string Name, string Description, string JsonSchema);

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Structured tool result (PLAN_v2 §2A.2) — never a bare string.</summary>
public sealed record ToolResult(
    bool Success,
    string? Output,
    string? Error,
    IReadOnlyList<ArtifactRef> Artifacts,
    IReadOnlyDictionary<string, object?> Metadata);

/// <summary>Factory helpers for <see cref="ToolResult"/>.</summary>
public static class ToolResults
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMeta = new Dictionary<string, object?>();

    public static ToolResult Ok(
        string? output = null,
        IReadOnlyList<ArtifactRef>? artifacts = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(true, output, null, artifacts ?? Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta);

    public static ToolResult Fail(string error)
        => new(false, null, error, Array.Empty<ArtifactRef>(), EmptyMeta);
}

/// <summary>The only surface a tool sees (PLAN_v2 §2A.1). No UI / Orchestrator back-channel.</summary>
public sealed record ToolContext(
    Guid TaskId,
    Guid RunId,
    Guid WorkspaceId,
    WorkContext Context,
    PermissionPolicy PermissionPolicy,
    string WorkspaceRoot,
    IArtifactStore Artifacts,
    IServiceProvider Services);

/// <summary>A capability the agent can invoke.</summary>
public interface ITool
{
    ToolDefinition Definition { get; }
    PermissionLevel RequiredLevel { get; }
    Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct);
}

/// <summary>Resolves and invokes tools by name.</summary>
public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> Definitions { get; }
    PermissionLevel RequiredLevelOf(string toolName);
    Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct);
}
