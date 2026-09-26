namespace Enactive.Tools;

using Enactive.Core.Permissions;
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
                   + "several. A file longer than " + MaxCharsPerFile + " characters is shown as "
                   + "its START and its END with the middle marked; use read_file for a window "
                   + "into the part between them. Preview scans at most " + FilePreview.MaxInputChars
                   + " characters plus one lookahead per file; larger files show a marked prefix "
                   + "preview with an unknown total line count.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.None, ParallelRead: true, Kind: ToolKind.Read, FileCoverage: FileCoverageBehavior.Read);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        => FileReadService.InvokeAsync(argumentsJson, ctx, ct, batchOnly: true);

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
