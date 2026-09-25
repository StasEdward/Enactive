namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Finds text in the workspace.
///
/// There was no search at all: to locate anything the agent had to walk <c>list_dir</c> and read
/// whole files, which spends the context window on material it will not use and is a large part of
/// why a run wanders before it starts working. Reading is not searching.
///
/// Bounded on every axis on purpose — a search that returns everything is a search that fills the
/// prompt with noise and pushes the instructions out of a small context window. The caps are stated
/// in the result rather than applied silently, so the model knows to narrow the query instead of
/// assuming it has seen all there is.
/// </summary>
public sealed class SearchFilesTool : ITool
{
    private const int MaxMatches = 100;
    private const int MaxLineChars = 240;
    private const int MaxOutputChars = 12000;

    /// <summary>
    /// Lines shown before and after each match unless the call says otherwise - like grep -C.
    ///
    /// <para>Measured 2026-09-24, run 71a546: of 55 searches, 30 were followed at once by a read_file
    /// of the same place, to see what was around the hit. That is a turn each, and every turn re-sends
    /// the whole conversation. Two lines either side answer most of those without one.</para>
    /// </summary>
    private const int DefaultContextLines = 2;

    /// <summary>The most context a call may ask for; past it the answer is a read, not a search.</summary>
    private const int MaxContextLines = 10;

    public ToolDefinition Definition { get; } = new(
        Name: "search_files",
        Description: "Search the workspace for a regular expression and return matching lines with "
                   + "their file and line number. Optionally restrict to file names matching a glob "
                   + "(e.g. \"*.cs\"). Use this to FIND things instead of reading files one by one. "
                   + "Build output and your own working area are left out of a whole-workspace "
                   + "sweep; to search inside one, name it with 'path' (e.g. \"" + WorkspaceGuard.ScratchPrefix
                   + "\" to search a long command output you saved there). 'path' may name a single "
                   + "FILE, which searches just that file. Each match comes with the lines around it "
                   + "('context'), so a search usually answers without a read_file after it.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.None);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? pattern, glob, subPath;
        bool ignoreCase;
        var context = DefaultContextLines;
        int? contextAsked = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            pattern = Text(root, "pattern");
            glob = Text(root, "glob");
            subPath = Text(root, "path");
            ignoreCase = !root.TryGetProperty("ignore_case", out var c)
                         || c.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("context", out var cx) && cx.ValueKind == JsonValueKind.Number
                && cx.TryGetInt32(out var asked))
            {
                contextAsked = asked;
                context = Math.Clamp(asked, 0, MaxContextLines);
            }
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

            // A pattern from a model can backtrack catastrophically. A timeout turns that into an
            // error message instead of a run that never returns.
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

        // A path that names ONE file is a search of that file, not a mistake.
        //
        // Measured 2026-09-20: a run asked for `error|failed|Passed!` in `build_output.txt` and was
        // told "Not a folder in this workspace: build_output.txt" - about a file sitting in the
        // workspace root, which it had listed a moment earlier. It then spent a turn on
        // `for %f in (build_output.txt) do @echo %~zf bytes & findstr /n /i ...` to get what it had
        // asked for. This is the same defect ListDirectoryTool was given the other half of a day
        // before: a message that says "wrong path" about a path that is right. Answering is better
        // than explaining, and searching one named file is a perfectly good question.
        var one = File.Exists(searchRoot);

        if (!one && !Directory.Exists(searchRoot))
            return ToolResults.NotFound($"Not a folder or file in this workspace: {subPath ?? "."}");

        var output = new StringBuilder();
        var matches = 0;
        var filesWithMatches = 0;
        var scanned = 0;
        var capped = false;

        // Files this search did not look inside. Counted rather than ignored: a search that skipped
        // the file holding the answer and reported "no matches in 40 file(s)" is a wrong answer
        // stated as a fact, and the model has no way to tell it apart from a real absence.
        var skippedLarge = 0;
        var skippedBinary = 0;

        try
        {
            // A file named outright is searched whatever the glob says: naming it IS the filter,
            // and the skip list is about where a walk WANDERS, not about what was asked for.
            foreach (var file in one ? new[] { searchRoot } : WorkspaceScan.Files(searchRoot, glob))
            {
                ct.ThrowIfCancellationRequested();

                var info = new FileInfo(file);
                if (info.Length > WorkspaceScan.MaxFileBytes) { skippedLarge++; continue; }
                if (WorkspaceScan.Binary(file)) { skippedBinary++; continue; }

                scanned++;
                var hit = false;

                var lines = await File.ReadAllLinesAsync(file, ct);
                var relative = WorkspaceScan.Relative(ctx.WorkspaceRoot, file);
                var lastShown = -1;   // the last line index already printed for this file

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!regex.IsMatch(lines[i])) continue;

                    hit = true;
                    matches++;

                    // A match line reads "path:N: text" as it always has; a context line "path-N- text",
                    // as grep writes it. Blocks that do not touch are separated by "--"; blocks that
                    // overlap are printed once.
                    var from = Math.Max(0, i - context);
                    var to = Math.Min(lines.Length - 1, i + context);
                    if (context > 0 && lastShown >= 0 && from > lastShown + 1)
                        output.Append("--\n");

                    for (var k = Math.Max(from, lastShown + 1); k <= to; k++)
                    {
                        var marker = k == i || regex.IsMatch(lines[k]) ? ':' : '-';
                        var shown = lines[k].Trim();
                        if (shown.Length > MaxLineChars) shown = shown[..MaxLineChars] + "…";
                        output.Append(relative).Append(marker).Append(k + 1).Append(marker)
                              .Append(' ').Append(shown).Append('\n');
                    }
                    lastShown = Math.Max(lastShown, to);

                    if (matches >= MaxMatches || output.Length >= MaxOutputChars) { capped = true; break; }
                }

                if (hit) filesWithMatches++;
                if (capped) break;
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
            return ToolResults.Fail($"Search failed: {ex.Message}");
        }

        var skipped = Skipped(skippedLarge, skippedBinary);

        if (matches == 0)
            return ToolResults.Ok(
                output: $"No matches for /{pattern}/ in {scanned} file(s)." + skipped,
                metadata: Metadata(0, 0, scanned, capped, skippedLarge, skippedBinary));

        if (capped)
            output.AppendLine($"… stopped at {matches} matches. Narrow the pattern or the glob to see the rest"
                            + (context > 0 ? ", or pass \"context\": 0 to list more matches in the same space." : "."));

        if (contextAsked is { } wanted && wanted > MaxContextLines)
            output.AppendLine($"(context is at most {MaxContextLines} lines either side; {wanted} was asked - "
                            + "for more, read_file the place.)");

        if (skipped.Length > 0)
            output.AppendLine(skipped.TrimStart('\n'));

        return ToolResults.Ok(
            output: output.ToString().TrimEnd(),
            metadata: Metadata(matches, filesWithMatches, scanned, capped, skippedLarge, skippedBinary));
    }

    /// <summary>
    /// What this search did not read, in the result itself.
    ///
    /// <para>The skips were a <c>continue</c> and nothing else: a file over the size ceiling, or one
    /// that looked binary, was passed over and the count of files "scanned" never included it. The
    /// answer then read "No matches for /X/ in 40 file(s)" — a sentence about 40 files presented as
    /// a fact about the workspace. A 3 MB generated file is exactly the kind that holds the string
    /// somebody is looking for.</para>
    /// </summary>
    private static string Skipped(int large, int binary)
    {
        if (large == 0 && binary == 0)
            return "";

        var parts = new List<string>(2);
        if (large > 0)
            parts.Add($"{large} file(s) larger than {WorkspaceScan.MaxFileBytes / (1024 * 1024)} MB");
        if (binary > 0)
            parts.Add($"{binary} binary file(s)");

        return $"\n… not searched: {string.Join(" and ", parts)}. "
             + "Read one directly with read_file if the answer might be in it.";
    }

    private static Dictionary<string, object?> Metadata(
        int matches, int files, int scanned, bool capped, int skippedLarge, int skippedBinary)
        => new()
        {
            ["matches"] = matches,
            ["files"] = files,
            ["filesScanned"] = scanned,
            ["truncated"] = capped,
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
        "pattern": { "type": "string", "description": "Regular expression to search for." },
        "glob": { "type": "string", "description": "Optional file-name filter, e.g. \"*.cs\". Default: every text file." },
        "path": { "type": "string", "description": "Optional folder to search, relative to the workspace root. Default: the whole workspace." },
        "ignore_case": { "type": "boolean", "description": "Case-insensitive. Default true." },
        "context": { "type": "integer", "description": "Lines shown before and after each match, like grep -C. Default 2 - usually enough to answer without opening the file. 0 lists matching lines only." }
      },
      "required": ["pattern"]
    }
    """;
}
