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
            return Task.FromResult(ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}"));
        }

        // The one folder inside the workspace's own state that tools may use is the working area,
        // and until now the way to it could not be WALKED: the root listing shows '.enactive/', the
        // worker's instructions say its own area is under it, and opening it was refused. The door
        // was shown and then shut - measured twice on 2026-09-21, in two live runs, and in one of
        // them the refused look was one of the two unresolved calls that failed the run.
        //
        // So a listing of the state folder answers with the part of it that is the model's: one
        // entry, and a sentence about the rest. Nothing is granted that was not reachable already -
        // '.enactive/scratch/' has been listable, readable and writable all along - what changes is
        // that it can be found by looking instead of only by knowing.
        if (WorkspaceGuard.IsReservedRoot(ctx.WorkspaceRoot, path))
        {
            // The area is made when a run starts (ScratchArea.Ensure), so the ordinary answer is
            // that it is there. Checked rather than asserted: the first version of this printed
            // "scratch/" unconditionally, which meant a listing promised a folder that list_dir
            // itself would then refuse - a defect of exactly the kind this carve-out was added to
            // remove. A test that wrote a file there first could never have caught it.
            var area = ScratchArea.PathIn(ctx.WorkspaceRoot);
            var there = Directory.Exists(area);

            return Task.FromResult(ToolResults.Ok(
                output: (there ? WorkspaceGuard.ScratchFolder + "/" + Environment.NewLine : "")
                      + $"(the rest of '{WorkspaceGuard.ReservedFolder}' is the workspace's own "
                      + "state - its undo journal, approvals and checkpoints - and tools do not go "
                      + $"in there. '{WorkspaceGuard.ScratchPrefix}/' is yours"
                      + (there ? "" : ", and writing anything into it will create it") + ".)",
                metadata: new Dictionary<string, object?> { ["entries"] = there ? 1 : 0 }));
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

            // NotFound, not Fail: a listing of somewhere that is not there has answered the question.
            //
            // But say WHICH thing is not there. A path that exists and is a file is not a wrong
            // path, and "Directory not found" sends the reader looking for a typo instead of at
            // the request - the same confusion delete_file guards against in the other direction,
            // where a directory would have been reported as a file that does not exist.
            //
            // Seen in a real run, 2026-09-20 16:05: list_dir on TicTacToe.csproj, told "Directory
            // not found", and the model called it AGAIN on the same path before working out for
            // itself that it wanted read_file. Two turns and two model calls spent on a message
            // that described the wrong problem.
            if (!onDisk && !proposed && File.Exists(full))
                return Task.FromResult(ToolResults.NotFound(
                    $"'{path}' is a file, not a directory — it IS there, so this is not a wrong "
                    + "path. Use read_file to read it, or list_dir on the folder that contains it."));

            if (!onDisk && !proposed)
                return Task.FromResult(ToolResults.NotFound($"Directory not found: {path ?? "."}"));

            entries.Sort(StringComparer.OrdinalIgnoreCase);

            var listing = entries.Count == 0 ? "(empty)" : string.Join("\n", entries);
            return Task.FromResult(ToolResults.Ok(
                output: listing,
                metadata: new Dictionary<string, object?> { ["count"] = entries.Count }));
        }
        // A read refused for being the workspace's own state has ANSWERED: the model asked
        // whether it could look there and was told no, definitively. Nothing is half-done and
        // there is nothing to retry, so it must not hold the step open. See ReservedPathException.
        catch (ReservedPathException)
        {
            return Task.FromResult(ToolResults.NotFound(ReservedPathException.Explanation));
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
