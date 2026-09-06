namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Reads a text file from the workspace (read-only, Observe level).
///
/// Reads a WINDOW of lines. Without one, a single large file silently ate the context window — the
/// whole file was loaded, then chopped at 8000 characters with no way to ask for the rest, so
/// anything past that point was simply unreachable and the model could not tell how much it had
/// missed. Now the window is explicit, the file's real length is always reported, and the result
/// says how to get the next part.
/// </summary>
public sealed class ReadFileTool : ITool
{
    private const int MaxChars = 8000;
    private const int DefaultLines = 400;

    public ToolDefinition Definition { get; } = new(
        Name: "read_file",
        Description: "Read a UTF-8 text file from the current workspace. Path is relative to the "
                   + "workspace root. Reads from 'offset' (1-based line, default 1) for 'limit' "
                   + "lines (default 400); the result says how many lines the file has.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path;
        int offset, limit;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
            offset = Number(root, "offset", 1);
            limit = Number(root, "limit", DefaultLines);
        }
        catch (JsonException ex)
        {
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (offset < 1) return ToolResults.Fail("'offset' is a 1-based line number, so it starts at 1.");
        if (limit < 1) return ToolResults.Fail("'limit' must be at least 1 line.");

        if (string.IsNullOrWhiteSpace(path))
            return ToolResults.Fail("'path' is required.");

        try
        {
            var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);

            // A staged write is content that exists as far as this run is concerned. Reading past it
            // to the disk is what made the instructed write-then-read verification report the OLD
            // file, or none at all, right after the agent had written it.
            var staged = await ctx.Artifacts.TryReadPendingAsync(path, ct);

            if (staged is null && !File.Exists(full))
                return ToolResults.Fail($"File not found: {path}");

            var text = staged ?? await File.ReadAllTextAsync(full, ct);
            var lines = text.Split('\n');
            var total = lines.Length;

            if (offset > total)
                return ToolResults.Fail(
                    $"'{path}' has {total} line(s); offset {offset} is past the end.");

            var window = lines.Skip(offset - 1).Take(limit).ToArray();
            var lastLine = offset + window.Length - 1;
            var body = string.Join('\n', window);

            // The character cap still applies inside the window — a few very long lines can exceed
            // it on their own — but now it is one of two limits the caller is told about, not the
            // silent end of the file.
            var clipped = body.Length > MaxChars;
            if (clipped) body = body[..MaxChars] + "\n… (line truncated at " + MaxChars + " characters)";

            var more = lastLine < total
                ? $"\n\n… showing lines {offset}–{lastLine} of {total}. Read on with offset {lastLine + 1}."
                : total > window.Length ? $"\n\n… showing lines {offset}–{lastLine} of {total}." : "";

            return ToolResults.Ok(
                output: body + more,
                metadata: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["bytes"] = text.Length,
                    ["totalLines"] = total,
                    ["firstLine"] = offset,
                    ["lastLine"] = lastLine,
                    ["truncated"] = clipped || lastLine < total,
                    // Say which version this is. "Proposed" and "on disk" are different facts, and a
                    // reviewer judging from evidence has to be able to tell them apart.
                    ["staged"] = staged is not null
                });
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not read '{path}': {ex.Message}");
        }
    }

    private static int Number(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed : fallback;

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path relative to the workspace root." },
        "offset": { "type": "integer", "description": "First line to read, 1-based. Default 1." },
        "limit": { "type": "integer", "description": "How many lines to read. Default 400." }
      },
      "required": ["path"]
    }
    """;
}
