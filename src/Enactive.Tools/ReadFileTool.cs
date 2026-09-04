namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>Reads a text file from the workspace (read-only, Observe level).</summary>
public sealed class ReadFileTool : ITool
{
    private const int MaxChars = 8000;

    public ToolDefinition Definition { get; } = new(
        Name: "read_file",
        Description: "Read a UTF-8 text file from the current workspace. Path is relative to the workspace root.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
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
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(path))
            return ToolResults.Fail("'path' is required.");

        try
        {
            var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);
            if (!File.Exists(full))
                return ToolResults.Fail($"File not found: {path}");

            var text = await File.ReadAllTextAsync(full, ct);
            var truncated = text.Length > MaxChars ? text[..MaxChars] + "\n… (truncated)" : text;

            return ToolResults.Ok(
                output: truncated,
                metadata: new Dictionary<string, object?> { ["path"] = path, ["bytes"] = text.Length });
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not read '{path}': {ex.Message}");
        }
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path relative to the workspace root." }
      },
      "required": ["path"]
    }
    """;
}
