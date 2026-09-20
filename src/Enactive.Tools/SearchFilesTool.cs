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
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxLineChars = 240;
    private const int MaxOutputChars = 12000;

    public ToolDefinition Definition { get; } = new(
        Name: "search_files",
        Description: "Search the workspace for a regular expression and return matching lines with "
                   + "their file and line number. Optionally restrict to file names matching a glob "
                   + "(e.g. \"*.cs\"). Use this to FIND things instead of reading files one by one. "
                   + "Build output and your own working area are left out of a whole-workspace "
                   + "sweep; to search inside one, name it with 'path' (e.g. \"" + WorkspaceGuard.ScratchPrefix
                   + "\" to search a long command output you saved there). 'path' may name a single "
                   + "FILE, which searches just that file.",
        JsonSchema: Schema);

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
        catch (ArgumentException ex) { return ToolResults.Fail(ex.Message); }

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
            foreach (var file in one ? new[] { searchRoot } : Enumerate(searchRoot, glob))
            {
                ct.ThrowIfCancellationRequested();

                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) { skippedLarge++; continue; }
                if (Binary(file)) { skippedBinary++; continue; }

                scanned++;
                var hit = false;

                var lineNumber = 0;
                foreach (var line in await File.ReadAllLinesAsync(file, ct))
                {
                    lineNumber++;
                    if (!regex.IsMatch(line)) continue;

                    hit = true;
                    matches++;

                    var shown = line.Trim();
                    if (shown.Length > MaxLineChars) shown = shown[..MaxLineChars] + "…";
                    output.Append(Relative(ctx.WorkspaceRoot, file)).Append(':').Append(lineNumber)
                          .Append(": ").AppendLine(shown);

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
            output.AppendLine($"… stopped at {matches} matches. Narrow the pattern or the glob to see the rest.");

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
            parts.Add($"{large} file(s) larger than {MaxFileBytes / (1024 * 1024)} MB");
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

    /// <summary>
    /// Files under the search root, skipping the places nobody means to search: the workspace's own
    /// state folder, and the build and dependency trees that would otherwise supply thousands of
    /// matches from code the user did not write.
    /// </summary>
    private static IEnumerable<string> Enumerate(string root, string? glob)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint   // a link is followed by nothing here
        };

        foreach (var file in Directory.EnumerateFiles(root, string.IsNullOrWhiteSpace(glob) ? "*" : glob, options))
        {
            if (!Skipped(root, file))
                yield return file;
        }
    }

    private static readonly string[] SkippedFolders =
        { WorkspaceGuard.ReservedFolder, "bin", "obj", "node_modules", ".git", ".vs", "dist", "packages" };

    /// <summary>
    /// Whether this file sits in one of the skipped folders, asked of the path BELOW the search
    /// root rather than of the whole path.
    ///
    /// <para>It used to split the absolute path, which made the names above mean two things they
    /// were never meant to mean:</para>
    /// <list type="number">
    /// <item><b>A workspace whose own location contains one of them was unsearchable.</b> A project
    /// under <c>C:\dev\packages\thing</c> or <c>~/bin/tool</c> matched on a segment of its own
    /// address, so every file was skipped and every search answered "No matches" - a wrong answer
    /// stated as a fact, which is the failure the skipped-file counters in this tool exist to
    /// prevent.</item>
    /// <item><b>Pointing a search AT a skipped folder could not work.</b> The worker's scratch area
    /// is under <c>.enactive/</c>, so a search rooted there matched on the root's own segment and
    /// returned nothing, always. Counting from the root gives the behaviour a person expects from
    /// every other search tool: the noisy places are left out of a sweep, and looked in when you
    /// name them. That is the whole of how scratch is reachable - there is no special case for it
    /// here, and none is wanted.</item>
    /// </list>
    /// </summary>
    private static bool Skipped(string searchRoot, string file)
    {
        var relative = Path.GetRelativePath(searchRoot, file);

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            foreach (var skip in SkippedFolders)
                if (string.Equals(segment, skip, WorkspaceGuard.Comparison))
                    return true;
        return false;
    }

    /// <summary>
    /// A NUL byte in the first few KB means this is not text. Cheap, and wrong only for files that
    /// would be unreadable in the output anyway.
    /// </summary>
    private static bool Binary(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[Math.Min(4096, (int)Math.Max(1, stream.Length))];
            var read = stream.Read(head);
            return head[..read].IndexOf((byte)0) >= 0;
        }
        catch { return true; }   // unreadable is as good as binary for this purpose
    }

    private static string Relative(string root, string file)
        => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

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
        "ignore_case": { "type": "boolean", "description": "Case-insensitive. Default true." }
      },
      "required": ["pattern"]
    }
    """;
}
