namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// How many files there are, how large they are, and how many lines each has — without reading a
/// line of any of them into the answer.
///
/// <para><b>The question this answers before a read.</b> <c>read_file</c> takes an offset and a
/// limit and reports the file's length only AFTER it has returned a window; <c>list_dir</c> gives
/// names. So "is this file 40 lines or 4000" costs a read, and "how much work is this folder"
/// costs one read per file. On 2026-09-12 a worker paged the same 10-line region of one file six
/// times, adjusting the offset each round, because nothing cheaper could tell it where the file
/// ended.</para>
///
/// <para>It is also the honest way to size a job before starting one: 140 files at 300 lines each
/// is a different plan from 12 files, and the difference is knowable in one call.</para>
///
/// <para>Line counts are omitted, not guessed, for a file that is binary or over the scan ceiling.
/// The byte size is still reported — it is the fact that made somebody ask.</para>
/// </summary>
public sealed class FileStatsTool : ITool
{
    private const int MaxFilesListed = 100;

    public ToolDefinition Definition { get; } = new(
        Name: "file_stats",
        Description: "List files with their size in bytes and line count, plus totals — without "
                   + "returning any file content. Use it to see how big something is BEFORE reading "
                   + "it, and to count how many files a job covers. Optionally filter by glob "
                   + "(e.g. \"*.md\") and folder.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? glob, subPath;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            glob = Text(root, "glob");
            subPath = Text(root, "path");
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        string scanRoot;
        try { scanRoot = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, subPath); }
        catch (ReservedPathException) { return ToolResults.NotFound(ReservedPathException.Explanation); }
        catch (ArgumentException ex) { return ToolResults.Fail(ex.Message); }

        if (!Directory.Exists(scanRoot))
            return ToolResults.NotFound($"Not a folder in this workspace: {subPath ?? "."}");

        var rows = new List<(string Path, long Bytes, int? Lines)>();
        long totalBytes = 0;
        var totalLines = 0;
        var counted = 0;
        var uncounted = 0;

        try
        {
            foreach (var file in WorkspaceScan.Files(scanRoot, glob))
            {
                ct.ThrowIfCancellationRequested();

                var info = new FileInfo(file);
                var bytes = info.Length;
                totalBytes += bytes;

                int? lines = null;
                if (bytes <= WorkspaceScan.MaxFileBytes && !WorkspaceScan.Binary(file))
                {
                    lines = await CountLinesAsync(file, ct);
                    totalLines += lines.Value;
                    counted++;
                }
                else
                {
                    uncounted++;
                }

                rows.Add((WorkspaceScan.Relative(ctx.WorkspaceRoot, file), bytes, lines));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not stat files: {ex.Message}");
        }

        // An empty folder, or a glob that matches nothing, has ANSWERED. Ok rather than NotFound:
        // the folder exists and the honest report of it is "nothing here".
        if (rows.Count == 0)
            return ToolResults.Ok(
                output: glob is { Length: > 0 }
                    ? $"No files matching \"{glob}\" under {subPath ?? "."}."
                    : $"No files under {subPath ?? "."}.",
                metadata: Metadata(0, 0, 0, 0, false));

        rows.Sort((a, b) => b.Bytes != a.Bytes
            ? b.Bytes.CompareTo(a.Bytes)
            : string.CompareOrdinal(a.Path, b.Path));

        var listed = Math.Min(rows.Count, MaxFilesListed);
        var output = new StringBuilder();
        output.Append(rows.Count).Append(" file(s), ").Append(Size(totalBytes))
              .Append(", ").Append(totalLines).Append(" line(s)");
        if (uncounted > 0)
            output.Append(" (").Append(uncounted).Append(" file(s) not line-counted: binary or over ")
                  .Append(WorkspaceScan.MaxFileBytes / (1024 * 1024)).Append(" MB)");
        output.AppendLine(". Largest first:");

        for (var i = 0; i < listed; i++)
        {
            var (path, bytes, lines) = rows[i];
            output.Append(path).Append("  ").Append(Size(bytes)).Append("  ")
                  .AppendLine(lines is null ? "— lines" : $"{lines} lines");
        }

        if (listed < rows.Count)
            output.AppendLine($"… {rows.Count - listed} smaller file(s) not listed; the totals above "
                            + "include all of them.");

        return ToolResults.Ok(
            output: output.ToString().TrimEnd(),
            metadata: Metadata(rows.Count, totalBytes, totalLines, counted, listed < rows.Count));
    }

    /// <summary>
    /// Lines by streaming, holding one counter. The whole file is never in memory — a 2 MB file
    /// costs the same as an empty one here, which is what makes this safe to run over a tree.
    /// </summary>
    private static async Task<int> CountLinesAsync(string file, CancellationToken ct)
    {
        var buffer = new char[8192];
        var lines = 1;
        var any = false;

        using var reader = new StreamReader(file);
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            any = true;
            for (var i = 0; i < read; i++)
                if (buffer[i] == '\n')
                    lines++;
        }

        // An empty file has no lines, not one. Counting from 1 is right only once there is content.
        return any ? lines : 0;
    }

    private static string Size(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
         : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB"
         : $"{bytes} B";

    private static Dictionary<string, object?> Metadata(
        int files, long bytes, int lines, int lineCounted, bool listingCapped)
        => new()
        {
            ["files"] = files,
            ["bytes"] = bytes,
            ["lines"] = lines,
            ["filesLineCounted"] = lineCounted,
            ["listingTruncated"] = listingCapped
        };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "glob": { "type": "string", "description": "Optional file-name filter, e.g. \"*.md\". Default: every file." },
        "path": { "type": "string", "description": "Optional folder, relative to the workspace root. Default: the whole workspace." }
      }
    }
    """;
}
