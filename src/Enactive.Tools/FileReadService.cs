namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Tools;
using static Enactive.Tools.ReadFileTool;
using static Enactive.Tools.ReadFilesTool;

/// <summary>Shared argument handling, workspace reads, output and coverage for both tool names.</summary>
internal static class FileReadService
{
    public static async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct, bool batchOnly)
    {
        string? path;
        int offset, limit;
        List<string>? several = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ToolResults.Unreadable("Arguments must be a JSON object.");
            if (batchOnly && (!root.TryGetProperty("paths", out var batchPaths)
                || batchPaths.ValueKind != JsonValueKind.Array))
                return ToolResults.Unreadable("'paths' is required and must be an ARRAY of file paths. For one file, use read_file.");
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
                if (!batchOnly && !string.IsNullOrWhiteSpace(path) && !several.Contains(path))
                    several.Insert(0, path);
            }
            offset = Number(root, "offset", 1);
            limit = Number(root, "limit", DefaultLines);
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (batchOnly || several is { Count: > 0 })
            return await ReadManyAsync(several ?? [], ctx, ct);

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

        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
        var last = (long)offset + limit - 1;
        var window = new StringBuilder();
        var windowLines = 0;
        var started = false;
        EnterLine(1);
        var scan = await FileTextScanner.ReadAsync(reader, (c, line) =>
        {
            if (c == '\n') EnterLine(line + 1);
            else if (line >= offset && line <= last && window.Length <= MaxChars)
                window.Append(c);
        }, inputLimit: null, ct);
        return new Slice(window.ToString(), scan.Lines, windowLines, scan.Characters);

        void EnterLine(int number)
        {
            if (number < offset || number > last)
                return;

            if (started && window.Length <= MaxChars)
                window.Append('\n');

            started = true;
            windowLines++;
        }
    }

    private static int Number(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed : fallback;

    private static async Task<ToolResult> ReadManyAsync(
        IReadOnlyList<string> paths, ToolContext ctx, CancellationToken ct)
    {
        if (paths.Count == 0)
            return ToolResults.Unreadable("'paths' is empty - name at least one file.");

        if (paths.Count > MaxFiles)
            return ToolResults.Unreadable(
                $"{paths.Count} paths, and at most {MaxFiles} may be read in one call. Ask for the "
                + "ones this question actually needs, then ask again for the rest.");

        // Resolved BEFORE anything is opened. One path that leaves the workspace makes the whole
        // call wrong, and a partial answer over a refused argument is worse than none: the model
        // would read four files and never learn the fifth was refused rather than empty.
        var resolved = new List<(string Relative, string Full)>();
        foreach (var relative in paths)
        {
            try { resolved.Add((relative, WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, relative))); }
            catch (ReservedPathException) { return ToolResults.NotFound(ReservedPathException.Explanation); }
            catch (ArgumentException ex) { return ToolResults.Unreadable($"'{relative}': {ex.Message}"); }
        }

        var sb = new StringBuilder();
        var budget = MaxCharsTotal;
        var found = 0;
        var missing = new List<string>();

        // What was shown of each file, for ReadLedger - a whole-file write of a file seen only as an
        // excerpt is the loss it exists to stop, and this is now a common way to read.
        var coverage = new List<FileCoverage>();
        var stagedPaths = new List<string>();

        try
        {
            foreach (var (relative, full) in resolved)
            {
                using var source = await WorkspaceReader.OpenAsync(ctx, relative, full, ct);
                if (source is null)
                {
                    missing.Add(relative);
                    continue;
                }

                if (budget <= 0)
                {
                    sb.AppendLine($"----- {relative} -----")
                      .AppendLine($"(not read: the {MaxCharsTotal} character budget for this call "
                                + "was used by the files above. Ask for this one on its own.)")
                      .AppendLine();
                    continue;
                }

                var room = Math.Min(MaxCharsPerFile, budget);
                var preview = await FilePreview.ReadAsync(source.Reader, room, ct);
                if (source.Staged) stagedPaths.Add(relative);

                // The START and the END, never just the start. Two reasons, and the second is the
                // stronger one. A file's top carries its namespace, usings and declaration and its
                // bottom its last member, so head-only throws away the half that says the file
                // ENDED - and a model reading a plausible file that simply stops has no way to
                // tell a cut from a truth. That is 9q's lesson for command output and 9by's for
                // the reviewer's excerpt.
                //
                // And the shape has to be ONE shape everywhere. A reader who learns "an excerpt is
                // the start and the end" and then meets one tool where it is not will misread it,
                // which is not hypothetical: 9cc is a label that survived a change of shape by a
                // day, and the reviewer believed the label over the text in front of it.
                //
                // FileHead rather than CommandHead because the reasoning behind two-fifths is about
                // commands - "a program reports its outcome last" - and a file has no outcome.
                var slice = preview.Text;
                budget -= slice.Length;
                found++;

                var lines = preview.Lines;
                coverage.Add(new FileCoverage(relative, lines,
                    preview.Complete && preview.Characters <= room ? lines : 0, preview.Complete));

                sb.AppendLine($"----- {relative} ({(preview.Complete ? "" : "at least ")}{preview.Characters} characters) -----")
                  .AppendLine(slice);

                if (!preview.Complete)
                    sb.AppendLine($"... (input scan stopped at the {FilePreview.MaxInputChars} character limit "
                        + "plus one lookahead character. Only a PREFIX was scanned; the file end and totalLines "
                        + "are unknown. Use read_file with offset/limit for a specific window; "
                        + "it scans the file to count exact lines.)");
                else if (preview.Characters > room)
                    sb.AppendLine($"... (shown: the start and the end, {slice.Length} of "
                                + $"{preview.Characters} characters. What is missing is the MIDDLE - "
                                + $"read_file '{relative}' with an offset to see it.)");

                sb.AppendLine();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not read the files: {ex.Message}");
        }

        // All of them absent is a lookup that ANSWERED - the rule read_file follows for one missing
        // path. Some absent and some found is an answer too, and the absent ones are NAMED rather
        // than passed over: a model that asked for five and got four back would otherwise have to
        // work out which one it never saw.
        if (found == 0)
            return ToolResults.NotFound(
                "None of these are there: " + string.Join(", ", missing)
                + ". Nothing went wrong - if you were guessing at where something lives, use "
                + "search_files or list_dir to find it.");

        if (missing.Count > 0)
            sb.AppendLine("----- not there: " + string.Join(", ", missing) + " -----");

        return ToolResults.Ok(sb.ToString().TrimEnd(),
            metadata: new Dictionary<string, object?>
            {
                ["files"] = coverage,
                ["stagedPaths"] = stagedPaths
            });
    }

}
