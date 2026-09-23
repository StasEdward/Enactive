namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Context;
using Enactive.Core.Tools;

/// <summary>
/// Reads several files in ONE call (read-only, Observe level).
///
/// <para><b>Why turns are the thing worth saving.</b> A run costs what it re-sends: every turn
/// carries the whole conversation again, so a step of 2N turns costs about four times a step of N
/// (§9bh). Measured 2026-09-24 on the wiki audit: 270 tool calls, of which <b>128 were
/// <c>read_file</c></b> and 119 were searches — 95% of a sixteen-minute run spent looking things up
/// one at a time. A page's claims point at four or five source files, and each one cost a round
/// trip through an eight-thousand-token transcript.</para>
///
/// <para><b>Why ours and not an MCP server's.</b> <c>desktop-commander</c> offers
/// <c>read_multiple_files</c>, and it was measured on 2026-09-23: offered alongside our 16 tools,
/// its 27 were called zero times. But adoption is not the deciding reason — a tool outside this
/// registry returns text and no <c>ArtifactRef</c>, so what it touches is invisible to the
/// reviewer's file list, to staging and revert, and to the write journal. For a READER that costs
/// nothing, which is exactly why this one tool is worth having and the whole server is not.</para>
///
/// <para><b>What it does not do.</b> No windowing. <c>read_file</c> is for "lines 400 to 460 of
/// this one file"; this is for "these five files", and a file too long for its share says so and
/// names <c>read_file</c> as the way to see the rest. Two tools with one job between them is how a
/// model ends up choosing badly (§9cb).</para>
/// </summary>
public sealed class ReadFilesTool : ITool
{
    /// <summary>
    /// How many paths one call may name. Past this the answer is longer than the reading it saved:
    /// the point is to collapse the four or five files a question actually touches, not to load a
    /// folder.
    /// </summary>
    internal const int MaxFiles = 10;

    /// <summary>
    /// Per file. Smaller than <c>read_file</c>'s 8000 on purpose — several files share one answer,
    /// and one long file must not crowd out the four that came with it.
    /// </summary>
    internal const int MaxCharsPerFile = 4000;

    /// <summary>
    /// Across the whole answer, so ten files at their own cap cannot make one reply of 40,000
    /// characters. Files are read in the order they were asked for, and any left past the budget
    /// say so rather than being dropped in silence.
    /// </summary>
    internal const int MaxCharsTotal = 16000;

    public ToolDefinition Definition { get; } = new(
        Name: "read_files",
        Description: "Read several UTF-8 text files from the current workspace in ONE call. Paths "
                   + "are relative to the workspace root, at most " + MaxFiles + ". Use this "
                   + "whenever a question touches more than one file - it costs one turn instead of "
                   + "several. Each file is shown whole up to " + MaxCharsPerFile + " characters and "
                   + "says so if it was cut; use read_file for a window into one long file.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        List<string> paths;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

            if (!doc.RootElement.TryGetProperty("paths", out var value)
                || value.ValueKind != JsonValueKind.Array)
                return ToolResults.Unreadable(
                    "'paths' is required and must be an ARRAY of file paths. For one file, use "
                    + "read_file.");

            paths = value.EnumerateArray()
                         .Where(e => e.ValueKind == JsonValueKind.String)
                         .Select(e => e.GetString()!)
                         .Where(p => p.Length > 0)
                         .ToList();
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

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

        try
        {
            foreach (var (relative, full) in resolved)
            {
                if (!File.Exists(full))
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

                var text = await File.ReadAllTextAsync(full, ct);
                var room = Math.Min(MaxCharsPerFile, budget);
                var slice = text.Length > room ? text[..room] : text;
                budget -= slice.Length;
                found++;

                sb.AppendLine($"----- {relative} ({text.Length} characters) -----")
                  .AppendLine(slice);

                if (slice.Length < text.Length)
                    sb.AppendLine($"... (cut after {slice.Length} of {text.Length} characters - "
                                + $"read_file '{relative}' with an offset to see the rest)");

                sb.AppendLine();
            }
        }
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

        return ToolResults.Ok(sb.ToString().TrimEnd());
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "paths": {
          "type": "array",
          "items": { "type": "string" },
          "description": "File paths relative to the workspace root, at most 10."
        }
      },
      "required": ["paths"]
    }
    """;
}
