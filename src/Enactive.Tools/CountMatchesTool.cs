namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// How many times a pattern occurs, and in which files — WITHOUT the matching lines.
///
/// <para><b>Why a second search tool.</b> <c>search_files</c> answers "show me the matches" and pays
/// for it: up to 100 lines of file content, which is the right price when the lines are the answer
/// and the wrong one when they are not. "How many files still call this", "does this string exist
/// anywhere", "which page mentions it" are questions whose answer is a number or a list of paths,
/// and paying a hundred lines for a number is how a context window fills with material nobody
/// reads.</para>
///
/// <para>Measured, 2026-09-12: a worker with only <c>read_file</c>,
/// <c>search_files</c> and <c>list_dir</c> spent 114 calls and 88k prompt tokens on an audit and
/// wrote nothing, because every question it had could only be answered by reading toward the
/// answer. 31 of those calls were searches whose results it then had to read. This tool exists so
/// that a question with a small answer costs a small answer.</para>
///
/// <para>The per-file counts are also the file LIST — there is no separate "which files contain
/// this" tool, because a table of <c>path: count</c> is that list with one extra number per row.</para>
/// </summary>
public sealed class CountMatchesTool : ITool
{
    /// <summary>Files named in the output. Beyond this the totals still hold; the listing says so.</summary>
    private const int MaxFilesListed = 60;

    public ToolDefinition Definition { get; } = new(
        Name: "count_matches",
        Description: "Count how many times a regular expression occurs in the workspace and in which "
                   + "files, WITHOUT returning the matching lines. Use this when the answer is a "
                   + "number or a list of files — \"how many\", \"does this exist anywhere\", \"which "
                   + "files mention it\" — and search_files only when you need to see the lines "
                   + "themselves.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.None, ParallelRead: true, Kind: ToolKind.Read);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? pattern, glob, subPath;
        bool ignoreCase;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            pattern = Text(root, "pattern");
            glob = Text(root, "glob");
            subPath = Text(root, "path");
            ignoreCase = !root.TryGetProperty("ignore_case", out var c)
                         || c.ValueKind != JsonValueKind.False;
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResults.Unreadable("'pattern' is required.");

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ToolResults.Unreadable($"'pattern' is not a valid regular expression: {ex.Message}");
        }

        string searchRoot;
        try { searchRoot = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, subPath); }
        catch (ReservedPathException) { return ToolResults.NotFound(ReservedPathException.Explanation); }
        catch (ArgumentException ex) { return ToolResults.Unreadable(ex.Message); }

        // A path that names ONE file is a question about that file, not a mistake. search_files
        // learnt this on 2026-09-20 - "answering is better than explaining, and searching one named
        // file is a perfectly good question" - and these two, written afterwards, inherited the
        // older behaviour anyway.
        //
        // Measured 2026-09-23 21:13:45: file_stats {"path":"Docs/DRIFT_ollama.md"} on the report the
        // step had just written, answered "Not a folder in this workspace" about a file that was
        // there. It cost nothing this time because a miss is an answer, but it is a wrong sentence
        // about a right path, which is the one thing a message must not be.
        var one = File.Exists(searchRoot);

        if (!one && !Directory.Exists(searchRoot))
            return ToolResults.NotFound($"Not a folder or file in this workspace: {subPath ?? "."}");

        var perFile = new List<(string Path, int Count, int Lines)>();
        var total = 0;
        var totalLines = 0;
        var scanned = 0;
        var skippedLarge = 0;
        var skippedBinary = 0;

        try
        {
            // Naming the file IS the filter, so the glob does not get to exclude it - the
            // same rule search_files applies, for the same reason.
            foreach (var file in one ? new[] { searchRoot } : WorkspaceScan.Files(searchRoot, glob))
            {
                ct.ThrowIfCancellationRequested();

                var info = new FileInfo(file);
                if (info.Length > WorkspaceScan.MaxFileBytes) { skippedLarge++; continue; }
                if (WorkspaceScan.Binary(file)) { skippedBinary++; continue; }

                scanned++;

                // Counted, not collected. Nothing here holds a line of file content, which is the
                // entire point: the cost of this call does not grow with what it finds.
                var inFile = 0;
                var linesInFile = 0;
                foreach (var line in await File.ReadAllLinesAsync(file, ct))
                {
                    var hits = regex.Matches(line).Count;
                    if (hits == 0) continue;
                    inFile += hits;
                    linesInFile++;
                }

                if (inFile == 0) continue;

                perFile.Add((WorkspaceScan.Relative(ctx.WorkspaceRoot, file), inFile, linesInFile));
                total += inFile;
                totalLines += linesInFile;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return ToolResults.Fail(
                "The pattern took too long to match — it is probably ambiguous enough to backtrack. "
                + "Anchor it or make it more specific.");
        }
        catch (OperationCanceledException) { throw; }
        // An argument the tool REFUSED before it looked at anything - a glob naming a path, a
        // path outside the workspace. Nothing was searched, so this is a sentence that did not
        // parse rather than work that went wrong, and Unreadable says so (DidNotRun). It matters
        // because a search names no file, is not a shell and changes nothing, so an open failure
        // recorded against it can be closed by NOTHING except repeating the identical bad call.
        // Measured 2026-09-23 00:15: search_files {"glob":"src/Enactive.Remote.*/*.cs"} was
        // refused with a perfectly good explanation, the model read the files another way, and
        // step 2 was marked Incomplete for it with steps 3 and 4 skipped.
        catch (ArgumentException ex)
        {
            return ToolResults.Unreadable(ex.Message);
        }

        catch (Exception ex)
        {
            return ToolResults.Fail($"Count failed: {ex.Message}");
        }

        var skipped = WorkspaceScan.SkippedNote(skippedLarge, skippedBinary) + (one ? "" : WorkspaceScan.IgnoredNote(searchRoot));

        // Zero is an ANSWER, not a failure: "this string is nowhere in the workspace" is exactly
        // what somebody deciding whether to delete a symbol wants to hear. Ok, not NotFound.
        if (total == 0)
            return ToolResults.Ok(
                output: $"No matches for /{pattern}/ in {scanned} file(s)." + skipped,
                metadata: Metadata(0, 0, 0, scanned, false, skippedLarge, skippedBinary));

        perFile.Sort((a, b) => b.Count != a.Count
            ? b.Count.CompareTo(a.Count)
            : string.CompareOrdinal(a.Path, b.Path));

        var listed = Math.Min(perFile.Count, MaxFilesListed);
        var output = new StringBuilder();
        output.Append(total).Append(" match(es) on ").Append(totalLines)
              .Append(" line(s) in ").Append(perFile.Count).Append(" of ").Append(scanned)
              .AppendLine(" file(s) scanned.");

        for (var i = 0; i < listed; i++)
            output.Append(perFile[i].Path).Append(": ").AppendLine(perFile[i].Count.ToString());

        if (listed < perFile.Count)
            output.AppendLine($"… {perFile.Count - listed} more file(s) not listed; the totals above "
                            + "count all of them. Narrow with 'glob' or 'path' to see the rest.");

        if (skipped.Length > 0)
            output.AppendLine(skipped.TrimStart('\n'));

        return ToolResults.Ok(
            output: output.ToString().TrimEnd(),
            metadata: Metadata(total, totalLines, perFile.Count, scanned,
                               listed < perFile.Count, skippedLarge, skippedBinary));
    }

    private static Dictionary<string, object?> Metadata(
        int matches, int lines, int files, int scanned, bool listingCapped,
        int skippedLarge, int skippedBinary)
        => new()
        {
            ["matches"] = matches,
            ["matchingLines"] = lines,
            ["files"] = files,
            ["filesScanned"] = scanned,
            // The COUNTS are complete even when the listing is not. Named apart from search_files'
            // "truncated", which means the opposite: there the numbers stop too.
            ["listingTruncated"] = listingCapped,
            ["skippedTooLarge"] = skippedLarge,
            ["skippedBinary"] = skippedBinary
        };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "pattern": { "type": "string", "description": "Regular expression to count." },
        "glob": { "type": "string", "description": "Optional file-name filter, e.g. \"*.cs\". Default: every text file." },
        "path": { "type": "string", "description": "Optional folder to scan, relative to the workspace root. Default: the whole workspace." },
        "ignore_case": { "type": "boolean", "description": "Case-insensitive. Default true." }
      },
      "required": ["pattern"]
    }
    """;
}
