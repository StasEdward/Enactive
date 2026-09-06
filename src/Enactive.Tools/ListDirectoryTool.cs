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

            var entries = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var onDisk = Directory.Exists(full);
            if (onDisk)
                foreach (var entry in Directory.EnumerateFileSystemEntries(full))
                {
                    var name = Directory.Exists(entry) ? Path.GetFileName(entry) + "/" : Path.GetFileName(entry);
                    if (seen.Add(name))
                        entries.Add(name);
                }

            // What the store is holding but has not written. A staged file that is not on disk yet
            // still belongs in the listing - otherwise a step "creates" a file and the next listing
            // says it is not there - and so does the FOLDER it sits in, which may not exist yet
            // either. Listing that folder used to fail outright with "Directory not found", so a
            // step could not look at what it had just proposed, and the root listing showed neither
            // the folder nor anything in it.
            var proposed = false;
            foreach (var (name, isDirectory) in PendingEntriesIn(ctx, full))
            {
                proposed = true;
                var display = isDirectory ? name + "/" : name;
                if (seen.Add(display))
                    entries.Add(display + "  (proposed, not yet applied)");
            }

            if (!onDisk && !proposed)
                return Task.FromResult(ToolResults.Fail($"Directory not found: {path ?? "."}"));

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
    /// What the artifact store is holding for this directory: the immediate child of it on the way
    /// to each pending path, and whether that child is a folder rather than the file itself. Empty
    /// for a store that writes straight through, which is the normal case.
    /// </summary>
    private static IEnumerable<(string Name, bool IsDirectory)> PendingEntriesIn(
        ToolContext ctx, string fullDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in ctx.Artifacts.PendingPaths)
        {
            string pendingFull;
            try { pendingFull = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, relative); }
            catch { continue; }

            string within;
            try { within = Path.GetRelativePath(fullDirectory, pendingFull); }
            catch { continue; }

            // Not under this directory at all: GetRelativePath answers with a climb, or with an
            // absolute path when the two share no root.
            if (within.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(within))
                continue;

            var segments = within.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0)
                continue;

            if (seen.Add(segments[0]))
                yield return (segments[0], segments.Length > 1);
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
