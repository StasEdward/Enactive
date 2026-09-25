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
                   + "lines (default 400); the result says how many lines the file has. To read SEVERAL "
                   + "files, pass 'paths' instead: independent reads in one call rather than one call each.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.None);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path;
        int offset, limit;
        List<string>? several = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;

            // Several files in one call. Measured 2026-09-24, run 71a546: 87 read_file calls and not
            // one read_files - the model reaches for this tool, so this tool is where reading several
            // at once has to be. The answer is read_files' own, budgets and all.
            if (root.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                several = ps.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 })
                            .Select(e => e.GetString()!)
                            .ToList();
                if (!string.IsNullOrWhiteSpace(path) && !several.Contains(path))
                    several.Insert(0, path);
            }
            offset = Number(root, "offset", 1);
            limit = Number(root, "limit", DefaultLines);
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (several is { Count: > 0 })
            return await new ReadFilesTool().InvokeAsync(
                JsonSerializer.Serialize(new { paths = several }), ctx, ct);

        if (offset < 1) return ToolResults.Unreadable("'offset' is a 1-based line number, so it starts at 1.");
        if (limit < 1) return ToolResults.Unreadable("'limit' must be at least 1 line.");

        if (string.IsNullOrWhiteSpace(path))
            return ToolResults.Unreadable("'path' is required - or 'paths', to read several files at once.");

        try
        {
            var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);

            // A staged write is content that exists as far as this run is concerned. Reading past it
            // to the disk is what made the instructed write-then-read verification report the OLD
            // file, or none at all, right after the agent had written it.
            using var source = await WorkspaceReader.OpenAsync(ctx, path, full, ct);

            // NotFound, not Fail: a read that finds nothing there has ANSWERED. Guessing at a path
            // and being told no is how a model explores a tree it has not seen.
            if (source is null)
                return ToolResults.NotFound($"File not found: {path}");

            // Only the window is held. This used to read the whole file into a string, split it into
            // an array of every line, and then keep a handful of them - so asking for twenty lines of
            // a two-gigabyte log cost two gigabytes plus the array, to answer with a screenful. A
            // staged write is already a string in memory and is windowed as it stands.
            var slice = await ReadWindowAsync(source.Reader, offset, limit, ct);

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

            // Where the cut falls, said exactly. It used to say "line truncated at 8000 characters"
            // whatever was cut - usually the WINDOW, part-way through some line - and the notice below
            // went on promising "lines 1-400, read on with offset 401", so the lines after the cut
            // were skipped by anyone who did as told. Measured 2026-09-24, run 71a546: a reviewer
            // believed the "line" in that sentence, told the step the 8,000 limit was per line, the
            // step corrected its report to say so, and the next review - reading the code - rejected
            // the correction. The message was the only thing that was wrong.
            var cutLine = lastLine;         // the line the shown text ends in
            var nextOffset = lastLine + 1;  // where reading on continues
            var fullyShown = lastLine;      // the last line shown whole
            var midLine = false;            // the cut fell inside a line, not between two
            int? tooLong = null;            // a line read_file cannot show whole, however it is asked
            if (clipped)
            {
                var kept = body[..MaxChars];

                // A cut that lands ON a line break cut between lines: every line kept is whole. An
                // 8,000-character line followed by a break was reported "longer than 8000" and
                // recorded as never seen, and reading on as told left it unreadable for good.
                var rest = body[MaxChars..];
                var between = rest.StartsWith('\n') || rest.StartsWith("\r\n", StringComparison.Ordinal) || rest == "\r"
                              || kept.EndsWith('\n');
                if (kept.EndsWith('\n'))
                    kept = kept[..^1];   // the next line had begun with nothing of it shown

                var newlines = kept.Count(ch => ch == '\n');
                cutLine = offset + newlines;

                if (between)
                {
                    body = kept + "\n… (this output is cut at " + MaxChars + " characters, after line "
                         + cutLine + " - the rest of the lines asked for is not shown)";
                    nextOffset = cutLine + 1;
                    fullyShown = cutLine;
                }
                else if (newlines == 0)
                {
                    // One line longer than the whole cap. Reading on "from this line" would return the
                    // same cut again, so the next read starts after it.
                    body = kept + "\n… (line " + cutLine + " is longer than " + MaxChars
                         + " characters on its own, and is cut here. read_file cannot show the rest of it; "
                         + "search_files or count_matches can look inside it.)";
                    nextOffset = cutLine + 1;
                    fullyShown = cutLine - 1;
                    midLine = true;
                    tooLong = cutLine;
                }
                else
                {
                    body = kept + "\n… (this output is cut at " + MaxChars + " characters, part-way "
                         + "through line " + cutLine + " - the rest of the lines asked for is not shown)";
                    nextOffset = cutLine;
                    fullyShown = cutLine - 1;
                    midLine = true;
                }
            }

            var more = clipped || lastLine < total
                // The notice used to end at "Read on with offset N" - an instruction to read again,
                // and the ONLY instruction on offer. A cut answer that names one way forward gets
                // that way taken: measured 2026-09-12, a model paged the same ten-line region of one
                // file six times, moving the offset and the limit each round, and the step ran
                // thirteen minutes past the point it had stopped making progress. Paging is right
                // when the file is being READ; it is the wrong move when something specific is being
                // looked for, and the alternative has to be named here, where the temptation is.
                ? $"\n\n… showing lines {offset}–{cutLine}{(midLine ? $" (line {cutLine} only in part)" : "")} of {total}. "
                  + (nextOffset <= total ? $"Read on with offset {nextOffset}. " : "")
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
                    // The last line shown WHOLE. ReadLedger decides from this whether a whole-file
                    // write of this file can be trusted, and a line cut part-way was not seen.
                    ["lastLine"] = fullyShown,
                    // The cursor, the SAME number the text tells the model to read on from - one
                    // source for both, so the reader and ReadLedger cannot disagree about it. Null when
                    // there is nothing further to read.
                    ["nextOffset"] = clipped || lastLine < total ? (nextOffset <= total ? nextOffset : null) : null,
                    // The line the shown text ends part-way through, when it was cut.
                    ["partialLine"] = midLine ? cutLine : null,
                    // A line too long to show whole, said outright rather than left to be inferred
                    // from the cursor: on the file's LAST line there is no cursor to infer it from,
                    // and the ledger then sent the model back to read the same cut again.
                    ["tooLongLine"] = tooLong,
                    ["truncated"] = clipped || lastLine < total,
                    // Say which version this is. "Proposed" and "on disk" are different facts, and a
                    // reviewer judging from evidence has to be able to tell them apart.
                    ["staged"] = source.Staged
                });
        }
        // A read refused for being the workspace's own state has ANSWERED: the model asked
        // whether it could look there and was told no, definitively. Nothing is half-done and
        // there is nothing to retry, so it must not hold the step open. See ReservedPathException.
        catch (ReservedPathException)
        {
            return ToolResults.NotFound(ReservedPathException.Explanation);
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
        TextReader reader, int offset, int limit, CancellationToken ct)
    {
        var last = offset + limit - 1;
        var window = new StringBuilder();
        var buffer = new char[8192];

        var line = 1;
        var totalLines = 1;
        var totalChars = 0;
        var windowLines = 0;
        var started = false;

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

    private static int Number(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed : fallback;

    /// <summary>How many lines a file has, counted the way this tool counts them: one more than its line breaks.</summary>
    internal static int LinesIn(string text) => 1 + text.Count(c => c == '\n');

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path relative to the workspace root." },
        "offset": { "type": "integer", "description": "First line to read, 1-based. Default 1." },
        "limit": { "type": "integer", "description": "How many lines to read. Default 400." },
        "paths": { "type": "array", "items": { "type": "string" }, "description": "Several files to read in ONE call, instead of 'path' - use it whenever the reads do not depend on each other. Each is shown from its start and its end within a shared budget." }
      }
    }
    """;
}
