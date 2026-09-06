namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Renames or moves a file inside the workspace.
///
/// The model could already do this with <c>run_command</c> — and that is the problem. A <c>move</c>
/// through the shell produces no artifact, no journal entry and no backup, so a rejected step could
/// not put the file back and the run's own record did not show that anything had moved. This does
/// the same job entirely inside the store: the destination is written through
/// <see cref="IArtifactStore.CreateAsync"/> and the source removed through
/// <see cref="IArtifactStore.RemoveAsync"/>, both journalled, both revertible.
///
/// It refuses to overwrite. A rename that lands on an existing file is either a mistake or a
/// deletion the model did not say it wanted, and neither should happen silently.
/// </summary>
public sealed class MoveFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "move_file",
        Description: "Rename or move a file inside the workspace. Both paths are relative to the "
                   + "workspace root. Fails if the destination already exists.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? from, to;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            from = Text(doc.RootElement, "from");
            to = Text(doc.RootElement, "to");
        }
        catch (JsonException ex)
        {
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(from)) return ToolResults.Fail("'from' is required.");
        if (string.IsNullOrWhiteSpace(to)) return ToolResults.Fail("'to' is required.");

        try
        {
            var source = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, from);
            var destination = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, to);

            if (string.Equals(source, destination, Enactive.Core.Context.WorkspaceGuard.Comparison))
                return ToolResults.Fail("'from' and 'to' are the same file.");

            if (!File.Exists(source))
                return ToolResults.Fail($"File not found: {from}");

            if (File.Exists(destination) || await ctx.Artifacts.TryReadPendingAsync(to, ct) is not null)
                return ToolResults.Fail(
                    $"'{to}' already exists. Moving onto it would destroy it — choose another name, "
                    + "or delete that file deliberately first.");

            var content = await File.ReadAllTextAsync(source, ct);

            // Destination first. If the removal then fails, the workspace holds both copies — which
            // is recoverable and visible. The other order risks losing the file entirely.
            var reference = await ctx.Artifacts.CreateAsync(
                to, ArtifactKind.FileSet, to,
                async stream => await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct),
                ct);

            try
            {
                await ctx.Artifacts.RemoveAsync(from, ct);
            }
            catch (NotSupportedException)
            {
                return ToolResults.Fail(
                    $"'{to}' was written, but this run stages changes for review and staging cannot "
                    + $"express a deletion, so '{from}' is still there. Apply the staged changes and "
                    + "remove the original yourself, or re-run without staging.");
            }

            return ToolResults.Ok(
                output: $"Moved '{from}' to '{to}' ({Encoding.UTF8.GetByteCount(content)} bytes). "
                      + "Both the new file and the removal can be undone.",
                artifacts: new[] { reference },
                metadata: new Dictionary<string, object?>
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["bytes"] = Encoding.UTF8.GetByteCount(content)
                });
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not move '{from}' to '{to}': {ex.Message}");
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "from": { "type": "string", "description": "Existing file path, relative to the workspace root." },
        "to": { "type": "string", "description": "New path, relative to the workspace root. Must not exist." }
      },
      "required": ["from", "to"]
    }
    """;
}
