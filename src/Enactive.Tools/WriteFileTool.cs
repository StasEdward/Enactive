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
                   + "Use a path relative to the workspace root. For large files, use small independent "
                   + "writes followed by append:true or edit_file calls. Each call must contain complete JSON arguments.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.Changed,
        ChangedPathArguments: ["path"],
        RepairsFileFailures: true, ProgressIdentity: ProgressIdentity.Action, Kind: ToolKind.Write, FileCoverage: FileCoverageBehavior.Replace);

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
            if (append) return await AppendAsync(path, content, ctx, ct);
            // Whether this REPLACES something has to be settled before the write, and it belongs in
            // the result: "Created X" for a file that already existed is a false statement, and it is
            // the exact statement the reviewer is handed as ground truth. A staged proposal counts as
            // existing content — that is what the next read would return.
            var snapshot = await TextFileEncoding.ReadSnapshotAsync(ctx.Artifacts, path,
                WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path), ct);
            var previousText = snapshot.Text;
            var replacing = previousText is not null;
            var encoding = snapshot.Encoding;
            long previousBytes = previousText is null ? 0 : encoding.GetPreamble().Length + encoding.GetByteCount(previousText);
            var endingsAdjusted = false;
            if (replacing && previousText is not null
                && LineEndings.RetypedFor(previousText, text) is { } retyped)
            {
                text = retyped;
                endingsAdjusted = true;
            }

            var newBytes = encoding.GetPreamble().Length + encoding.GetByteCount(text);

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

            Func<Stream, Task> write = async stream => await stream.WriteAsync(TextFileEncoding.Encode(text, encoding), ct);
            var reference = ctx.Artifacts.CanCheckVersion
                ? await ctx.Artifacts.CreateCheckedAsync(path, ArtifactKind.FileSet, path, write, snapshot.Version, ct)
                : await ctx.Artifacts.CreateAsync(path, ArtifactKind.FileSet, path, write, ct);

            // Bytes, not "chars": the two differ the moment the content is not ASCII, and the result
            // line and the metadata disagreeing by four is a puzzle nobody should have to solve.
            var bytes = newBytes;

            // Only claim the old version is recoverable when it actually is. Taking the backup is
            // best-effort by design, and this sentence is what the reviewer is handed as ground
            // truth — a promise made every time is a promise the reviewer cannot check.
            var restorable = replacing && ctx.Artifacts.CanRestore(path);
            // restore_file puts back how the file was before the RUN - not the version this write displaced. Named only
            // for a file the run found: for one an earlier step made, it has nothing to put back, and offering it there
            // sends the model to a tool that refuses, as git did in run a19a2c.
            var foundByRun = restorable && (await ctx.Artifacts.BeforeRunAsync(path, ct)).State == BeforeRunState.Kept;

            return ToolResults.Ok(
                output: replacing
                    ? (restorable
                        ? $"REPLACED the existing file '{path}' ({bytes} bytes). Its previous version was kept and can be restored by the user from the run"
                          + (foundByRun ? "; restore_file puts the file back as it was before the run." : " - there is no tool for it, and git has only what was committed.")
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

        catch (Exception ex) when (ex is not OperationCanceledException)
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
    private static async Task<ToolResult> AppendAsync(string path, string added, ToolContext ctx, CancellationToken ct)
    {
        var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);
        bool existed = false;
        long bytes = 0, addedBytes = 0;
        var direct = ctx.Artifacts.CanAppend;
        async Task WriteTail(Stream? existing, Stream output)
        {
            await using var input = direct ? existing : await ctx.Artifacts.TryOpenPendingAsync(path, ct)
                ?? (File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan) : null);
            existed = input is not null;
            var encoding = input is null ? new UTF8Encoding(false, true) : await TextFileEncoding.Detect(input, ct);
            var crlf = false;
            char last = '\0';
            var hasText = false;
            if (input is not null)
            {
                if (!input.CanSeek) throw new NotSupportedException("Appending requires a seekable artifact stream.");
                input.Position = encoding.GetPreamble().Length;
                // Validate strictly and detect endings with bounded memory, including CRLF across
                // buffer boundaries. No ReadLine: one log line can itself be hundreds of MB.
                using (var reader = new StreamReader(input, encoding, false, 8192, leaveOpen: true))
                {
                    var buffer = new char[8192];
                    int count;
                    while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
                        for (var i = 0; i < count; i++)
                        {
                            crlf |= last == '\r' && buffer[i] == '\n';
                            last = buffer[i];
                            hasText = true;
                        }
                }
                bytes = input.Length;
                input.Position = 0;
                if (!direct) await input.CopyToAsync(output, 81920, ct);
            }
            else
            {
                var preamble = encoding.GetPreamble();
                await output.WriteAsync(preamble, ct);
                bytes = preamble.Length;
            }
            var text = existed ? LineEndings.RetypedFor(crlf ? "\r\n" : "\n", added) ?? added : added;
            if (hasText && text.Length > 0 && last != '\n'
                && !text.StartsWith('\n') && !text.StartsWith("\r\n", StringComparison.Ordinal))
                text = (crlf ? "\r\n" : "\n") + text;
            addedBytes = encoding.GetByteCount(text);
            // No preamble in the appended segment; the original bytes were copied unchanged.
            using var writer = new StreamWriter(output, WithoutPreamble(encoding), 8192, leaveOpen: true);
            await writer.WriteAsync(text.AsMemory(), ct);
            await writer.FlushAsync(ct);
            bytes += addedBytes;
        }
        var reference = direct
            ? await ctx.Artifacts.AppendAsync(path, ArtifactKind.FileSet, path, WriteTail, ct)
            : await ctx.Artifacts.CreateAsync(path, ArtifactKind.FileSet, path, output => WriteTail(null, output), ct);
        return ToolResults.Ok(
            existed ? $"APPENDED {addedBytes} bytes to the end of '{path}' (now {bytes} bytes). Everything that was already in it is unchanged."
                : $"Created new file '{path}' ({bytes} bytes).",
            artifacts: [reference], metadata: new Dictionary<string, object?>
            {
                ["path"] = path, ["bytes"] = bytes, ["replacedExistingFile"] = false,
                ["appended"] = existed, ["previousVersionRecoverable"] = existed && ctx.Artifacts.CanRestore(path)
            });
    }

    private static Encoding WithoutPreamble(Encoding encoding) => encoding.CodePage switch
    {
        1200 => new UnicodeEncoding(false, false, true),
        1201 => new UnicodeEncoding(true, false, true),
        12000 => new UTF32Encoding(false, false, true),
        12001 => new UTF32Encoding(true, false, true),
        _ => new UTF8Encoding(false, true)
    };

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
