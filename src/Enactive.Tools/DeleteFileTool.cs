namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Deletes a file inside the workspace.
///
/// <para>Added last, and deliberately so. Every other file tool here names something the model
/// could already do badly; this one names something it could not do at all, and the workspace was
/// none the worse for it - a model that cannot delete cannot delete the wrong thing. What changed
/// is that the shells are denied for a run started from the web, so "remove this file" went from
/// being done dangerously to not being done, and the honest answer to that is a tool that removes
/// a file the way this codebase removes things: through the store, journalled, revertible.</para>
///
/// <para><b>It always asks.</b> <see cref="RequiresApproval"/> is true, so the permission gate
/// turns an Allow into an Ask at every tier including Autonomous - the one file tool that does.
/// The others produce something a person can look at afterwards and judge; this one produces an
/// absence, and an absence is the hardest thing to notice. That is the same argument the shells
/// get, for the same reason.</para>
///
/// <para>It removes a FILE. Not a directory, not a tree: "delete the folder" is a different request
/// with a different blast radius, and a tool that quietly did both would be answering a question
/// nobody asked.</para>
/// </summary>
public sealed class DeleteFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "delete_file",
        Description: "Delete a file inside the workspace. The path is relative to the workspace "
                   + "root. Deletes one file, never a directory. Always asks first.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    /// <summary>Always, at every tier. See the note on this class.</summary>
    public bool RequiresApproval => true;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            path = Text(doc.RootElement, "path");
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(path)) return ToolResults.Unreadable("'path' is required.");

        try
        {
            var target = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);

            // Before "does it exist": a directory that exists would otherwise be reported as a file
            // that does not, which sends the reader looking for a typo instead of at the request.
            if (Directory.Exists(target))
                return ToolResults.Fail(
                    $"'{path}' is a directory. This deletes one file at a time; removing a folder "
                    + "and everything under it is not something this tool will do.");

            if (!File.Exists(target))
                return ToolResults.Fail($"File not found: {path}");

            // Asked BEFORE anything happens, and for the same reason move_file asks it: a run that
            // stages its changes has no way to express "gone", so the deletion could only be
            // pretended. Saying so is better than a proposal nobody can apply.
            if (!ctx.Artifacts.CanRemove)
                return ToolResults.Fail(
                    $"'{path}' is still there: this run stages changes for review, and staging cannot "
                    + "express a deletion. Delete it yourself once the staged changes are applied, or "
                    + "re-run without staging.");

            // Through the store, so the removal is journalled and a rejected step puts the file
            // back. A File.Delete here would be the shell's version of this tool with a nicer name.
            var bytes = new FileInfo(target).Length;
            await ctx.Artifacts.RemoveAsync(path, ct);

            return ToolResults.Ok(
                output: $"Deleted '{path}' ({bytes} bytes). This can be undone.",
                metadata: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["bytes"] = bytes
                });
        }
        catch (NotSupportedException)
        {
            return ToolResults.Fail(
                $"'{path}' is still there: this run stages changes for review, and staging cannot "
                + "express a deletion.");
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not delete '{path}': {ex.Message}");
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File to delete, relative to the workspace root." }
      },
      "required": ["path"]
    }
    """;
}
