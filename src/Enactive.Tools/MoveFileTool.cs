namespace Enactive.Tools;

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
///
/// The bytes are copied, never decoded. A move that re-encodes its file is not a move: it returns
/// something else under the old name and deletes the original to prove it.
/// </summary>
public sealed class MoveFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "move_file",
        Description: "Rename or move a file inside the workspace. Both paths are relative to the "
                   + "workspace root. Fails if the destination already exists.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.Changed,
        ChangedPathArguments: ["from", "to"],
        RepairsFileFailures: true, ProgressIdentity: ProgressIdentity.Action, Kind: ToolKind.Relocate, FileCoverage: FileCoverageBehavior.Move);

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
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(from)) return ToolResults.Unreadable("'from' is required.");
        if (string.IsNullOrWhiteSpace(to)) return ToolResults.Unreadable("'to' is required.");

        try
        {
            var source = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, from);
            var destination = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, to);

            if (string.Equals(source, destination, Enactive.Core.Context.WorkspaceGuard.Comparison))
                return ToolResults.Unreadable("'from' and 'to' are the same file.");

            // Asked BEFORE anything is written. A move is a write and then a removal, and a store
            // that cannot express a removal — staging, whose proposals are a file's next content and
            // have no way to say "gone" — used to accept the first half and throw on the second: the
            // tool reported failure, correctly, and left the destination proposal behind for someone
            // to Apply. The failure of the second half must leave nothing, and the way to have that
            // is not to start. The catch below stays as a belt for a store that answers wrongly.
            if (!ctx.Artifacts.CanRemove)
                return ToolResults.Fail(
                    $"'{from}' is still there and nothing was written: this run stages changes for "
                    + "review, and staging cannot express a deletion, so the move could only half "
                    + "happen. Write the new file and delete the old one yourself once the staged "
                    + "changes are applied, or re-run without staging.");

            if (!File.Exists(source))
                return ToolResults.Fail($"File not found: {from}");

            if (File.Exists(destination) || await ctx.Artifacts.TryReadPendingAsync(to, ct) is not null)
                return ToolResults.Fail(
                    $"'{to}' already exists. Moving onto it would destroy it — choose another name, "
                    + "or restore its contents with an allowed write/edit operation.");

            // BYTES, not text. This read the file with ReadAllTextAsync and wrote UTF-8 back, which
            // is not a move: a PNG or a zip came out with every invalid UTF-8 byte replaced by U+FFFD,
            // a UTF-16 file was transcoded, a BOM could vanish — and then the original was deleted and
            // the result reported as success. A move must hand back the same file it was given.
            long moved = 0;

            // Destination first. If the removal then fails, the workspace holds both copies — which
            // is recoverable and visible. The other order risks losing the file entirely.
            var reference = await ctx.Artifacts.CreateAsync(
                to, ArtifactKind.FileSet, to,
                async stream =>
                {
                    await using var input = new FileStream(
                        source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 81920, useAsync: true);
                    await input.CopyToAsync(stream, ct);
                    moved = input.Length;
                },
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
                output: $"Moved '{from}' to '{to}' ({moved} bytes). "
                      + "Both the new file and the removal can be undone.",
                artifacts: new[] { reference },
                metadata: new Dictionary<string, object?>
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["bytes"] = moved
                });
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
            return ToolResults.Unreadable(ex.Message);
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
