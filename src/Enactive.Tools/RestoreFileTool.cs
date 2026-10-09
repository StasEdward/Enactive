namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Puts a file back exactly as it was before the run first changed it.
///
/// <para><b>Why.</b> Run bb77e810, 2026-10-09: a request asked for code to be broken on purpose and put back. "Put it
/// back" had no tool - the engine kept the file's earlier version, and told the worker "there is no tool for it" - so
/// the worker had to undo its own edit by hand, and a step titled "Restore behaviour" took "restore" for "fix": it
/// rewrote the code to a version the request had never had, then 21 older tests to suit it. Putting back is not
/// something to retype from memory. It is a copy of bytes the engine already holds.</para>
///
/// <para>Through the store, as every write is: journalled, and undone with the step that made it. A file the run made
/// is not removed here - there was nothing before it to put back, and taking a file away is delete_file's, which asks
/// first. A change made by a command was never recorded, and is said so rather than guessed at.</para>
/// </summary>
public sealed class RestoreFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "restore_file",
        Description: "Put a file back exactly as it was before this run first changed it - byte for byte, from the copy the "
                   + "engine kept. Use it to undo a change made on purpose (a temporary break, an experiment) instead of "
                   + "editing the file back by hand. Path relative to the workspace root.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.Changed,
        ChangedPathArguments: ["path"],
        ProgressIdentity: ProgressIdentity.Action, Kind: ToolKind.Write, FileCoverage: FileCoverageBehavior.Replace,
        RestoresRunStart: true);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            path = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("path", out var p)
                   && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(path)) return ToolResults.Unreadable("'path' is required.");

        try
        {
            var target = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);
            var before = await ctx.Artifacts.BeforeRunAsync(path, ct);
            switch (before.State)
            {
                case BeforeRunState.Untouched:
                    return ToolResults.Ok(
                        output: $"'{path}' has not been changed by this run's file tools, so it is as it was - nothing was put back. "
                                + "A change made by a command is not recorded, and this tool cannot undo it.") with { WorkspaceEffect = WorkspaceEffect.None };
                case BeforeRunState.Absent:
                    return ToolResults.Fail(
                        $"'{path}' was made by this run - there was no such file before it, so there is nothing to put back. "
                        + "If it should go, that is delete_file's to do.");
                case BeforeRunState.Lost:
                    return ToolResults.Fail($"'{path}' was changed by this run, and how it was before could not be kept - it cannot be put back.");
                case BeforeRunState.Unknown:
                    // Not "its changes are staged" alone: a file a command changed where the workspace was not measured - a
                    // folder git ignores - is not known either (run 89aa8d1b, 2026-10-09; see RunStart).
                    return ToolResults.Fail($"This run has no record of how '{path}' was before it - a staged change, or a change made "
                        + "by a command where the workspace is not measured, is not recorded - so it cannot be put back here.");
            }

            var original = before.Content!;
            if (File.Exists(target) && (await File.ReadAllBytesAsync(target, ct)).AsSpan().SequenceEqual(original))
                return ToolResults.Ok(output: $"'{path}' is already exactly as it was before the run.") with { WorkspaceEffect = WorkspaceEffect.None };

            var reference = await ctx.Artifacts.CreateAsync(path, ArtifactKind.FileSet, path,
                stream => stream.WriteAsync(original, ct).AsTask(), ct);
            return ToolResults.Ok(
                output: $"Put '{path}' back exactly as it was before the run ({original.Length} bytes).",
                artifacts: [reference],
                metadata: new Dictionary<string, object?> { ["path"] = path, ["bytes"] = original.Length });
        }
        catch (ArgumentException ex)
        {
            return ToolResults.Unreadable(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ToolResults.Fail($"Could not put '{path}' back: {ex.Message}");
        }
    }

    private const string Schema = """
        {"type":"object","properties":{"path":{"type":"string","description":"The file to put back, relative to the workspace root."}},"required":["path"]}
        """;
}
