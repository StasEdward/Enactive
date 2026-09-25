namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>Creates or overwrites a text file inside the current workspace. Produces a FileSet artifact.</summary>
public sealed class WriteFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "write_file",
        Description: "Create or overwrite a text file inside the current workspace. "
                   + "Use a path relative to the workspace root.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.Changed);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path;
        string? content;
        bool allowShrink;
        bool append;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            allowShrink = root.TryGetProperty("allow_shrink", out var a) && a.ValueKind == JsonValueKind.True;
            append = root.TryGetProperty("append", out var ap) && ap.ValueKind == JsonValueKind.True;
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(path))
            return ToolResults.Unreadable("'path' is required.");

        // A MISSING content is an error, not an empty file. The schema requires it, so its absence
        // means the arguments are not what the model meant to send - and the old default silently
        // turned that into a truncation of whatever was already at that path. (It is how a
        // mis-merged read+write pair emptied a file: the write arrived carrying the read's
        // arguments, which have no content.) An explicit "" still writes an empty file.
        if (content is null)
            return ToolResults.Unreadable(
                "'content' is required. To empty a file, pass an empty string explicitly.");

        var text = content;

        try
        {
            // Whether this REPLACES something has to be settled before the write, and it belongs in
            // the result: "Created X" for a file that already existed is a false statement, and it is
            // the exact statement the reviewer is handed as ground truth. A staged proposal counts as
            // existing content — that is what the next read would return.
            bool replacing;
            long previousBytes = 0;

            // The text that is there now, when there is any. Needed for two things: how big it is,
            // and which line endings it uses.
            string? previousText = null;
            try
            {
                // A staged proposal counts as the existing content - that is what the next read
                // would return - so it is what a replacement is measured against too.
                var pending = await ctx.Artifacts.TryReadPendingAsync(path, ct);
                if (pending is not null)
                {
                    replacing = true;
                    previousText = pending;
                    previousBytes = Encoding.UTF8.GetByteCount(pending);
                }
                else
                {
                    var existing = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);
                    replacing = File.Exists(existing);
                    if (replacing)
                    {
                        previousBytes = new FileInfo(existing).Length;
                        previousText = await File.ReadAllTextAsync(existing, ct);
                    }
                }
            }
            catch
            {
                // A path the guard refuses fails properly in CreateAsync below, with its own message.
                replacing = false;
                previousBytes = 0;
                previousText = null;
            }

            // A REPLACEMENT is written in the endings the file already uses - the same rule
            // edit_file has followed since a user found it unusable on Windows, and for the same
            // reason: a carriage return is invisible in what read_file returns, so a model cannot
            // send one and every whole-file rewrite of a CRLF document silently converted it. Every
            // line then shows as changed and a repository with autocrlf churns, for an edit meant to
            // touch one line.
            //
            // A NEW file keeps exactly what it was given: there are no endings to be consistent
            // with, and the caller's choice is the only one there is.
            //
            // This was carried as open in FIX_PLAN §9b, pinned by a test, on the argument that
            // write_file is also how endings get changed deliberately. That argument does not
            // survive contact with the caller: a model cannot see a carriage return, so it cannot
            // ask for one either - which makes every conversion here accidental, and the deliberate
            // case indistinguishable from the accident. A shell command converts a file on purpose.
            // APPEND: the new text goes after what is there. The way to add a section to a report
            // without retyping the report - see the schema, and FIX_PLAN 9cq for what retyping cost.
            // The ADDITION takes the file's endings before it is joined on: once joined, the text
            // already contains the file's own CRLFs and would look converted when it is not.
            var addedBytes = Encoding.UTF8.GetByteCount(text);
            var appending = append && replacing && previousText is not null;
            if (appending)
                text = Joined(previousText!, LineEndings.RetypedFor(previousText!, text) ?? text);

            var endingsAdjusted = false;
            if (!appending && replacing && previousText is not null
                && LineEndings.RetypedFor(previousText, text) is { } retyped)
            {
                text = retyped;
                endingsAdjusted = true;
            }

            var newBytes = Encoding.UTF8.GetByteCount(text);

            if (replacing && !append && !allowShrink && WouldLoseMostOfTheFile(previousBytes, newBytes))
                return ToolResults.Fail(
                    $"Refusing to replace '{path}': the new content is {newBytes} bytes against "
                    + $"{previousBytes} already there, so most of the file would be gone. This is "
                    + "almost always a whole-file rewrite attempted for a change to one PART of it - "
                    + "use edit_file, which replaces an exact passage and does not make you reproduce "
                    + "the rest. If the file really is meant to shrink this much, send the same "
                    + "write_file call again with \"allow_shrink\": true.");

            // The same loss without the shrink. Measured 2026-09-24 21:06, run a19a2c: a step told to
            // APPEND pages 4-6 to a report replaced it with pages 4-6 alone - 10,938 bytes over
            // 9,696, so the size check above saw a file that grew. Step 1's findings were gone; the
            // model noticed, went looking in git for a copy the file never had, and retyped them
            // from its context. What was lost is the file's LINES, so that is what is counted.
            if (replacing && !append && !allowShrink && previousText is not null
                && previousBytes >= ShrinkGuardFloorBytes
                && LinesKept(previousText, text) is var (kept, had) && kept * 2 < had)
                return ToolResults.Fail(
                    $"Refusing to replace '{path}': the new content keeps {kept} of the {had} lines already "
                    + $"there, so most of what the file holds ({previousBytes} bytes) would be gone. If you "
                    + "meant to ADD to the file, send only the new part with \"append\": true; to change part "
                    + "of it, use edit_file. If it really is meant to be replaced by different content, send "
                    + "the same write_file call again with \"allow_shrink\": true.");

            var reference = await ctx.Artifacts.CreateAsync(
                path, ArtifactKind.FileSet, path,
                async stream =>
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(bytes, ct);
                },
                ct);

            // Bytes, not "chars": the two differ the moment the content is not ASCII, and the result
            // line and the metadata disagreeing by four is a puzzle nobody should have to solve.
            var bytes = newBytes;

            // Only claim the old version is recoverable when it actually is. Taking the backup is
            // best-effort by design, and this sentence is what the reviewer is handed as ground
            // truth — a promise made every time is a promise the reviewer cannot check.
            var restorable = replacing && ctx.Artifacts.CanRestore(path);

            return ToolResults.Ok(
                output: append && replacing
                    ? $"APPENDED {addedBytes} bytes to the end of '{path}' (now {bytes} bytes). Everything "
                      + "that was already in it is unchanged."
                    : replacing
                    ? (restorable
                        ? $"REPLACED the existing file '{path}' ({bytes} bytes). Its previous version was kept and can be restored by the user from the run - there is no tool for it, and git has only what was committed."
                        : $"REPLACED the existing file '{path}' ({bytes} bytes). Its previous version could NOT be backed up and is gone.")
                      + (endingsAdjusted
                          ? " Written with the line endings the file already used, so only the lines "
                          + "you actually changed show as changed."
                          : "")
                    : $"Created new file '{path}' ({bytes} bytes).",
                artifacts: new[] { reference },
                metadata: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["bytes"] = bytes,
                    ["replacedExistingFile"] = replacing && !append,
                    ["appended"] = append && replacing,
                    ["previousVersionRecoverable"] = restorable
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
            return ToolResults.Fail($"Could not write '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Whether replacing a file this way would throw most of it away.
    ///
    /// <para>Found on 2026-09-07. Asked to add one menu entry to a 414-line page, a 12B model read
    /// 400 of those lines and called write_file with 168 lines - 21164 bytes replaced by 7982. It
    /// was reported as a plain success, twice in a row, and the user's page was gone both times.
    /// The model was not misbehaving: no worker had been given edit_file, so the only way it had to
    /// change one line was to retype the document, and retyping a document from context is
    /// summarising it. edit_file is now handed out; this is the second half, because the same shape
    /// of loss is possible whenever a model chooses write_file on something long.</para>
    ///
    /// <para>Small files are exempt: below <see cref="ShrinkGuardFloorBytes"/> a rewrite is cheap,
    /// a model reproduces it reliably, and halving one is ordinary editing rather than a symptom.
    /// The check is on BYTES, so it costs a stat rather than a read.</para>
    /// </summary>
    internal static bool WouldLoseMostOfTheFile(long previousBytes, long newBytes)
        => previousBytes >= ShrinkGuardFloorBytes && newBytes * 2 < previousBytes;

    /// <summary>
    /// How many of the file's distinct non-blank lines the new content still has, of how many it
    /// had. Compared trimmed, so a re-indented line still counts as kept; a rewrite that changes most
    /// lines is exactly what this is meant to make deliberate.
    /// </summary>
    internal static (int Kept, int Had) LinesKept(string previous, string next)
    {
        static HashSet<string> Distinct(string text)
            => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);

        var had = Distinct(previous);
        var now = Distinct(next);
        return (had.Count(now.Contains), had.Count);
    }

    /// <summary>
    /// What is there, then what is added - on a line of its own. A report that ends without a line
    /// break would otherwise run its last line straight into the new section's heading.
    /// </summary>
    private static string Joined(string previous, string added)
        => previous.Length == 0 || added.Length == 0
           || previous.EndsWith('\n') || added.StartsWith('\n') || added.StartsWith("\r\n", StringComparison.Ordinal)
            ? previous + added
            : previous + (previous.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") + added;

    /// <summary>Below this, a file is short enough that rewriting it whole is not the risky act.</summary>
    private const int ShrinkGuardFloorBytes = 2_000;

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path relative to the workspace root, e.g. list_files.py" },
        "content": { "type": "string", "description": "The full text content of the file - or, with append, only the text to add at its end." },
        "append": { "type": "boolean", "description": "Set true to ADD content to the end of the file instead of replacing it - send only the new text. Use this to add a section to a report or a log: it does not make you retype what is already there. Creates the file if it does not exist." },
        "allow_shrink": { "type": "boolean", "description": "Set true only when an existing file is genuinely meant to lose most of its content - to shrink, or to be replaced by different text. Without it a replacement that drops most of a file is refused, because that is nearly always a whole-file rewrite of a file that should have been edited in part, or added to with append." }
      },
      "required": ["path", "content"]
    }
    """;
}
