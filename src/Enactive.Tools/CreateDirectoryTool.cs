namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Creates a folder inside the workspace.
///
/// <c>write_file</c> already creates the folders a file needs, so this is for the case where the
/// structure comes first — and for taking the job away from <c>run_command</c>, where a stray
/// <c>mkdir</c> ran outside every path check this app has.
///
/// A folder is not content, so there is no artifact and nothing to journal: an empty directory
/// carries nothing to lose, and removing one on revert would be guessing at intent. That is stated
/// in the result rather than left for the model to assume, since every other write in this run CAN
/// be put back.
/// </summary>
public sealed class CreateDirectoryTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "create_directory",
        Description: "Create a folder inside the workspace, including any missing parents. "
                   + "Path is relative to the workspace root. Succeeds if it already exists. "
                   + "Not needed before write_file, which creates folders on its own.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

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
            return Task.FromResult(ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResults.Unreadable("'path' is required."));

        try
        {
            var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);

            if (File.Exists(full))
                return Task.FromResult(ToolResults.Fail($"'{path}' is a file, not a folder."));

            var existed = Directory.Exists(full);
            if (!existed) Directory.CreateDirectory(full);

            return Task.FromResult(ToolResults.Ok(
                output: existed
                    ? $"'{path}' already exists."
                    : $"Created folder '{path}'. Folders are not tracked for undo — only file contents are.",
                metadata: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["alreadyExisted"] = existed
                }));
        }
        // A path WorkspacePaths would not resolve - it leaves the workspace, or it is not a path.
        // Nothing was opened, so this is an argument refused rather than an operation that went
        // wrong, and the difference is the whole of 9bj-9bm. It also fixes the wording: without
        // this the refusal arrives as "Could not read 'x': …", which reads as a read that failed.
        //
        // ArgumentException in this block comes from that resolution; the file system throws
        // IOException and UnauthorizedAccessException, which fall through to the handler below.
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResults.Unreadable(ex.Message));
        }

        catch (Exception ex)
        {
            return Task.FromResult(ToolResults.Fail($"Could not create '{path}': {ex.Message}"));
        }
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Folder path relative to the workspace root." }
      },
      "required": ["path"]
    }
    """;
}
