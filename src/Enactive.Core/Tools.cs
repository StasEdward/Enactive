namespace Enactive.Core.Tools;

using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Permissions;

/// <summary>Describes a tool to the model (name + description + JSON Schema for arguments).</summary>
public sealed record ToolDefinition(string Name, string Description, string JsonSchema);

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// Argument names the ENGINE has to know about as well as the tool that reads them. Kept here
/// because Core is the only layer both sides share, and a name spelled twice is a name that drifts.
/// </summary>
public static class ToolArguments
{
    /// <summary>
    /// The exit codes a caller declares it expects from a command (see the command-running tools).
    ///
    /// <para>The engine cares because it says how to READ a result, not what to do: a command and
    /// the same command declaring that 1 is an answer are the same action, and a run-again-and-
    /// declare that produced a different identity would leave the first failure open forever.</para>
    /// </summary>
    public const string ExpectedExitCodes = "expectedExitCodes";
}

/// <summary>Structured tool result (PLAN_v2 §2A.2) — never a bare string.</summary>
/// <param name="IsAnswer">
/// Set on a FAILED result that is nevertheless the answer to what was asked: the file the model
/// wanted to look at is not there, and being told so is the whole information it needed. Nothing
/// is left unfinished and there is nothing to retry, so a step must not be held open by it —
/// unless such lookups are ALL the step has to show, which is a step that did nothing.
///
/// <para>Only a LOOKUP can set this. "File not found" from <c>edit_file</c> or <c>move_file</c> is
/// the opposite — an edit that did not happen — and those keep it false.</para>
/// </param>
public sealed record ToolResult(
    bool Success,
    string? Output,
    string? Error,
    IReadOnlyList<ArtifactRef> Artifacts,
    IReadOnlyDictionary<string, object?> Metadata,
    bool IsAnswer = false);

/// <summary>Factory helpers for <see cref="ToolResult"/>.</summary>
public static class ToolResults
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMeta = new Dictionary<string, object?>();

    public static ToolResult Ok(
        string? output = null,
        IReadOnlyList<ArtifactRef>? artifacts = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(true, output, null, artifacts ?? Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta);

    /// <summary>
    /// A failed tool call. <paramref name="output"/> and <paramref name="metadata"/> are kept on the
    /// failure path too: a command that fails is exactly when its stdout/stderr and exit code matter,
    /// and dropping them would force the model to guess what went wrong.
    /// </summary>
    public static ToolResult Fail(
        string error,
        string? output = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(false, output, error, Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta);

    /// <summary>
    /// A lookup whose target is not there. The call did not succeed, and it is not unfinished
    /// either: "there is no such file" is what the model asked to be told.
    ///
    /// <para>Reported with a log, 2026-09-07: a step listed a folder, read the one file in it,
    /// guessed at a second name, was told that file does not exist, wrote the documentation it was
    /// asked to write — and the run was failed anyway, because an unresolved <c>read_file</c> held
    /// the step open and the next step was skipped behind it. Guessing at a path and being told no
    /// is how exploring works.</para>
    ///
    /// <para>With one condition, which two shipping tests insisted on: a step whose ENTIRE record is
    /// lookups that found nothing has produced nothing, and saying "Done" over that is the defect
    /// the unresolved-failure guard exists for. One call that WORKED is the difference.</para>
    ///
    /// <para>For LOOKUPS only — <c>read_file</c>, <c>list_dir</c>, <c>search_files</c>. A write,
    /// an edit or a move that cannot find its target uses <see cref="Fail"/>: there, the missing
    /// file means the work did not happen.</para>
    /// </summary>
    public static ToolResult NotFound(
        string error,
        string? output = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new(false, output, error, Array.Empty<ArtifactRef>(), metadata ?? EmptyMeta, IsAnswer: true);
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
    bool RequiresApproval => false;
    ToolDefinition Definition { get; }
    PermissionLevel RequiredLevel { get; }
    Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct);
}

/// <summary>Resolves and invokes tools by name.</summary>
public interface IToolRegistry
{
    bool RequiresApprovalOf(string toolName) => false;
    IReadOnlyList<ToolDefinition> Definitions { get; }
    PermissionLevel RequiredLevelOf(string toolName);
    Task<ToolResult> InvokeAsync(ToolCall call, ToolContext ctx, CancellationToken ct);
}
