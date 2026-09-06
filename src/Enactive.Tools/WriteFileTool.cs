namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>Creates or overwrites a text file inside the current workspace. Produces a FileSet artifact.</summary>
public sealed class WriteFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "write_file",
        Description: "Create or overwrite a text file inside the current workspace. "
                   + "Use a path relative to the workspace root.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path;
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (JsonException ex)
        {
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(path))
            return ToolResults.Fail("'path' is required.");

        // A MISSING content is an error, not an empty file. The schema requires it, so its absence
        // means the arguments are not what the model meant to send - and the old default silently
        // turned that into a truncation of whatever was already at that path. (It is how a
        // mis-merged read+write pair emptied a file: the write arrived carrying the read's
        // arguments, which have no content.) An explicit "" still writes an empty file.
        if (content is null)
            return ToolResults.Fail(
                "'content' is required. To empty a file, pass an empty string explicitly.");

        var text = content;

        try
        {
            // Whether this REPLACES something has to be settled before the write, and it belongs in
            // the result: "Created X" for a file that already existed is a false statement, and it is
            // the exact statement the reviewer is handed as ground truth. A staged proposal counts as
            // existing content — that is what the next read would return.
            bool replacing;
            try
            {
                replacing = await ctx.Artifacts.TryReadPendingAsync(path, ct) is not null
                            || File.Exists(WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path));
            }
            catch
            {
                // A path the guard refuses fails properly in CreateAsync below, with its own message.
                replacing = false;
            }

            var reference = await ctx.Artifacts.CreateAsync(
                path, ArtifactKind.FileSet, path,
                async stream =>
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(bytes, ct);
                },
                ct);

            // Bytes, not "chars": the two differ the moment the content is not ASCII, and the result
            // line and the metadata disagreeing by four is a puzzle nobody should have to solve.
            var bytes = Encoding.UTF8.GetByteCount(text);

            // Only claim the old version is recoverable when it actually is. Taking the backup is
            // best-effort by design, and this sentence is what the reviewer is handed as ground
            // truth — a promise made every time is a promise the reviewer cannot check.
            var restorable = replacing && ctx.Artifacts.CanRestore(path);

            return ToolResults.Ok(
                output: replacing
                    ? restorable
                        ? $"REPLACED the existing file '{path}' ({bytes} bytes). Its previous version was kept and can be restored."
                        : $"REPLACED the existing file '{path}' ({bytes} bytes). Its previous version could NOT be backed up and is gone."
                    : $"Created new file '{path}' ({bytes} bytes).",
                artifacts: new[] { reference },
                metadata: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["bytes"] = bytes,
                    ["replacedExistingFile"] = replacing,
                    ["previousVersionRecoverable"] = restorable
                });
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not write '{path}': {ex.Message}");
        }
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path relative to the workspace root, e.g. list_files.py" },
        "content": { "type": "string", "description": "The full text content of the file." }
      },
      "required": ["path", "content"]
    }
    """;
}
