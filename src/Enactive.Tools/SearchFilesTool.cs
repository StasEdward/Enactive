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
                   + "(e.g. \"*.cs\"). Use this to FIND things instead of reading files one by one.",
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
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResults.Fail("'pattern' is required.");

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
            return ToolResults.Fail($"'pattern' is not a valid regular expression: {ex.Message}");
        }

        string searchRoot;
        try { searchRoot = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, subPath); }
        catch (ArgumentException ex) { return ToolResults.Fail(ex.Message); }

        if (!Directory.Exists(searchRoot))
            return ToolResults.Fail($"Not a folder in this workspace: {subPath ?? "."}");

        var output = new StringBuilder();
        var matches = 0;
        var filesWithMatches = 0;
        var scanned = 0;
        var capped = false;

        try
        {
            foreach (var file in Enumerate(searchRoot, glob))
            {
                ct.ThrowIfCancellationRequested();

                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes || Binary(file))
                    continue;

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

        if (matches == 0)
            return ToolResults.Ok(
                output: $"No matches for /{pattern}/ in {scanned} file(s).",
                metadata: new Dictionary<string, object?> { ["matches"] = 0, ["filesScanned"] = scanned });

        if (capped)
            output.AppendLine($"… stopped at {matches} matches. Narrow the pattern or the glob to see the rest.");

        return ToolResults.Ok(
            output: output.ToString().TrimEnd(),
            metadata: new Dictionary<string, object?>
            {
                ["matches"] = matches,
                ["files"] = filesWithMatches,
                ["filesScanned"] = scanned,
                ["truncated"] = capped
            });
    }

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
            if (!Skipped(file))
                yield return file;
        }
    }

    private static readonly string[] SkippedFolders =
        { WorkspaceGuard.ReservedFolder, "bin", "obj", "node_modules", ".git", ".vs", "dist", "packages" };

    private static bool Skipped(string file)
    {
        foreach (var segment in file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
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
