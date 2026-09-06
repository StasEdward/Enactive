namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Context;
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
                .ToList();

            // A staged file that is not on disk yet still belongs in the listing — otherwise a step
            // "creates" a file and the next listing says it is not there. It is marked, because
            // proposed and written are different things.
            foreach (var pendingName in PendingNamesIn(ctx, full))
                if (!entries.Contains(pendingName, StringComparer.OrdinalIgnoreCase))
                    entries.Add(pendingName + "  (proposed, not yet applied)");

            entries.Sort(StringComparer.OrdinalIgnoreCase);

            var listing = entries.Count == 0 ? "(empty)" : string.Join("\n", entries);
            return Task.FromResult(ToolResults.Ok(
                output: listing,
                metadata: new Dictionary<string, object?> { ["count"] = entries.Count }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResults.Fail($"Could not list '{path ?? "."}': {ex.Message}"));
        }
    }

    /// <summary>
    /// File names the artifact store is holding for this directory but has not written yet. Empty
    /// for a store that writes straight through, which is the normal case.
    /// </summary>
    private static IEnumerable<string> PendingNamesIn(ToolContext ctx, string fullDirectory)
    {
        foreach (var relative in ctx.Artifacts.PendingPaths)
        {
            string pendingFull;
            try { pendingFull = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, relative); }
            catch { continue; }

            var directory = Path.GetDirectoryName(pendingFull);
            if (directory is not null
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(directory),
                    Path.TrimEndingDirectorySeparator(fullDirectory),
                    WorkspaceGuard.Comparison))
            {
                yield return Path.GetFileName(pendingFull);
            }
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
