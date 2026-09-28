namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Copies a file inside the workspace.
///
/// <para><b>Why this had to exist.</b> Copying was the one everyday file operation with no tool
/// behind it, so a model asked to copy something did the only thing it could: read the file and
/// write the bytes back out under the new name. <c>read_file</c> stops at 8000 characters - and
/// says so - and the model wrote what it had. An 11 KB page came out as an 8 KB one, with the
/// closing tags added, so the result was a well-formed HTML document missing a quarter of its
/// body. Nothing downstream could tell it from a real copy: it parsed, it looked complete, and the
/// content reviewer called it internally consistent, which it was.</para>
///
/// <para>That is the worst shape a defect can have here - not a failure, a plausible result. The
/// shell used to hide it, because <c>Copy-Item</c> works; with shells denied for a run started
/// from the web, the gap surfaced the first time somebody asked for a copy.</para>
///
/// <para>Like <see cref="MoveFileTool"/>: the bytes are streamed, never decoded, so an image or a
/// UTF-16 file survives; the destination goes through <see cref="IArtifactStore.CreateAsync"/>, so
/// it is journalled and a rejected step can take it back; and it refuses to overwrite, because a
/// copy that lands on an existing file is a deletion nobody asked for.</para>
///
/// <para>Unlike a move, it removes nothing - so unlike a move it needs no
/// <see cref="IArtifactStore.CanRemove"/> and works in a run that stages its changes.</para>
/// </summary>
public sealed class CopyFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "copy_file",
        Description: "Copy a file inside the workspace, whole and byte for byte. Both paths are "
                   + "relative to the workspace root. Fails if the destination already exists. "
                   + "Prefer this over reading a file and writing it back: reading is truncated for "
                   + "large files and the copy would silently be partial.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.Changed,
        ChangedPathArguments: ["to"],
        RepairsFileFailures: false, ProgressIdentity: ProgressIdentity.Action, Kind: ToolKind.Relocate);

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

            await using var input = await ctx.Artifacts.TryOpenPendingAsync(from, ct)
                ?? (File.Exists(source) ? new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 81920, useAsync: true) : null);
            if (input is null) return ToolResults.Fail($"File not found: {from}");
            await using var pendingDestination = await ctx.Artifacts.TryOpenPendingAsync(to, ct);
            if (File.Exists(destination) || pendingDestination is not null)
                return ToolResults.Fail(
                    $"'{to}' already exists. Copying onto it would destroy it — choose another name, "
                    + "or restore its contents with an allowed write/edit operation.");

            // BYTES, streamed, never decoded. The whole point of this tool is that the content does
            // not pass through anything that could shorten or re-encode it - not the model's
            // context, not a UTF-8 round trip. A copy that returns something else is not a copy.
            long copied = 0;

            async Task Copy(Stream stream)
            {
                await input.CopyToAsync(stream, ct);
                copied = input.Length;
            }
            var reference = ctx.Artifacts.CanCheckVersion
                ? await ctx.Artifacts.CreateCheckedAsync(to, ArtifactKind.FileSet, to, Copy, new ArtifactVersion(null), ct)
                : await ctx.Artifacts.CreateAsync(to, ArtifactKind.FileSet, to, Copy, ct);

            return ToolResults.Ok(
                output: $"Copied '{from}' to '{to}' ({copied} bytes). The new file can be undone.",
                artifacts: new[] { reference },
                metadata: new Dictionary<string, object?>
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["bytes"] = copied
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
            return ToolResults.Fail($"Could not copy '{from}' to '{to}': {ex.Message}");
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
        "to": { "type": "string", "description": "Path for the copy, relative to the workspace root. Must not exist." }
      },
      "required": ["from", "to"]
    }
    """;
}
