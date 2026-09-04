namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>Lists the entries of a directory in the workspace (read-only, Observe level).</summary>
public sealed class ListDirectoryTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "list_dir",
        Description: "List files and folders in a workspace directory. Path is relative to the workspace root "
                   + "(defaults to '.'). Folders end with '/'.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            path = doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ToolResults.Fail($"Invalid arguments JSON: {ex.Message}"));
        }

        try
        {
            var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);
            if (!Directory.Exists(full))
                return Task.FromResult(ToolResults.Fail($"Directory not found: {path ?? "."}"));

            var entries = Directory.EnumerateFileSystemEntries(full)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Directory.Exists(p) ? Path.GetFileName(p) + "/" : Path.GetFileName(p))
                .ToArray();

            var listing = entries.Length == 0 ? "(empty)" : string.Join("\n", entries);
            return Task.FromResult(ToolResults.Ok(
                output: listing,
                metadata: new Dictionary<string, object?> { ["count"] = entries.Length }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResults.Fail($"Could not list '{path ?? "."}': {ex.Message}"));
        }
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Directory path relative to the workspace root. Defaults to '.'." }
      }
    }
    """;
}
