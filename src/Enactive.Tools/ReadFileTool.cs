namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Context;
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
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (offset < 1) return ToolResults.Unreadable("'offset' is a 1-based line number, so it starts at 1.");
        if (limit < 1) return ToolResults.Unreadable("'limit' must be at least 1 line.");

        if (string.IsNullOrWhiteSpace(path))
            return ToolResults.Unreadable("'path' is required.");

        try
        {
            var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);

            // A staged write is content that exists as far as this run is concerned. Reading past it
            // to the disk is what made the instructed write-then-read verification report the OLD
            // file, or none at all, right after the agent had written it.
            var staged = await ctx.Artifacts.TryReadPendingAsync(path, ct);

            // NotFound, not Fail: a read that finds nothing there has ANSWERED. Guessing at a path
            // and being told no is how a model explores a tree it has not seen.
            if (staged is null && !File.Exists(full))
                return ToolResults.NotFound($"File not found: {path}");

            // Only the window is held. This used to read the whole file into a string, split it into
            // an array of every line, and then keep a handful of them - so asking for twenty lines of
            // a two-gigabyte log cost two gigabytes plus the array, to answer with a screenful. A
            // staged write is already a string in memory and is windowed as it stands.
            var slice = staged is null
                ? await ReadWindowAsync(full, offset, limit, ct)
                : Window(staged, offset, limit);

            var total = slice.TotalLines;

            // NotFound for the same reason as a missing file: this ANSWERED. "Is there more after
            // line 800?" — "no, the file has 263 lines" is the whole information the caller wanted,
            // and it is how paging through a file of unknown length works. Reported 2026-09-07
            // 21:35: two of these, both right after a successful read of the same file, left a step
            // Incomplete and skipped the two steps behind it.
            if (offset > total)
                return ToolResults.NotFound(
                    $"'{path}' has {total} line(s); offset {offset} is past the end.");

            var lastLine = offset + slice.WindowLines - 1;
            var body = slice.Text;

            // The character cap still applies inside the window — a few very long lines can exceed
            // it on their own — but now it is one of two limits the caller is told about, not the
            // silent end of the file.
            var clipped = body.Length > MaxChars;
            if (clipped) body = body[..MaxChars] + "\n… (line truncated at " + MaxChars + " characters)";

            var more = lastLine < total
                // The notice used to end at "Read on with offset N" - an instruction to read again,
                // and the ONLY instruction on offer. A cut answer that names one way forward gets
                // that way taken: measured 2026-09-12, a model paged the same ten-line region of one
                // file six times, moving the offset and the limit each round, and the step ran
                // thirteen minutes past the point it had stopped making progress. Paging is right
                // when the file is being READ; it is the wrong move when something specific is being
                // looked for, and the alternative has to be named here, where the temptation is.
                ? $"\n\n… showing lines {offset}–{lastLine} of {total}. Read on with offset {lastLine + 1}. "
                  + "If you are looking for something rather than reading this file, search_files or "
                  + "count_matches will find it without paging."
                : total > slice.WindowLines ? $"\n\n… showing lines {offset}–{lastLine} of {total}." : "";

            return ToolResults.Ok(
                output: body + more,
                metadata: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["bytes"] = slice.TotalChars,
                    ["totalLines"] = total,
                    ["firstLine"] = offset,
                    ["lastLine"] = lastLine,
                    ["truncated"] = clipped || lastLine < total,
                    // Say which version this is. "Proposed" and "on disk" are different facts, and a
                    // reviewer judging from evidence has to be able to tell them apart.
                    ["staged"] = staged is not null
                });
        }
        // A read refused for being the workspace's own state has ANSWERED: the model asked
        // whether it could look there and was told no, definitively. Nothing is half-done and
        // there is nothing to retry, so it must not hold the step open. See ReservedPathException.
        catch (ReservedPathException)
        {
            return ToolResults.NotFound(ReservedPathException.Explanation);
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not read '{path}': {ex.Message}");
        }
    }

    /// <summary>The requested lines, and what it takes to describe where they came from.</summary>
    private readonly record struct Slice(string Text, int TotalLines, int WindowLines, int TotalChars);

    /// <summary>
    /// Reads a file once, keeping only the requested lines. Lines are separated exactly as
    /// <c>Split('\n')</c> separated them - a file ending in a newline has a final empty line, and a
    /// CR before the LF stays where it was - because the window this returns has to be the same text
    /// the previous implementation returned, not a tidied version of it.
    /// </summary>
    private static async Task<Slice> ReadWindowAsync(
        string fullPath, int offset, int limit, CancellationToken ct)
    {
        var last = offset + limit - 1;
        var window = new StringBuilder();
        var buffer = new char[8192];

        var line = 1;
        var totalLines = 1;
        var totalChars = 0;
        var windowLines = 0;
        var started = false;

        using var reader = new StreamReader(fullPath);

        EnterLine(1);

        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                totalChars++;

                if (c == '\n')
                {
                    line++;
                    totalLines++;
                    EnterLine(line);
                    continue;
                }

                // One character past the display cap is enough to know it was exceeded; everything
                // beyond that is discarded rather than gathered and then thrown away.
                if (line >= offset && line <= last && window.Length <= MaxChars)
                    window.Append(c);
            }
        }

        return new Slice(window.ToString(), totalLines, windowLines, totalChars);

        void EnterLine(int number)
        {
            if (number < offset || number > last)
                return;

            if (started)
                window.Append('\n');

            started = true;
            windowLines++;
        }
    }

    /// <summary>The same window over content already in memory - a staged write.</summary>
    private static Slice Window(string text, int offset, int limit)
    {
        var lines = text.Split('\n');
        var window = lines.Skip(offset - 1).Take(limit).ToArray();
        return new Slice(string.Join('\n', window), lines.Length, window.Length, text.Length);
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
