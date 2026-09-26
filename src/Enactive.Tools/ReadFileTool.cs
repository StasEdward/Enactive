namespace Enactive.Tools;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Reads a text file from the workspace (read-only, Observe level).
///
/// Reads a WINDOW of lines. Without one, a single large file silently ate the context window — the
/// whole file was loaded, then chopped at 8000 characters with no way to ask for the rest, so
/// anything past that point was simply unreachable and the model could not tell how much it had
/// missed. Now the window is explicit, the file's real length is always reported, and the result
/// says how to get the next part.
/// </summary>
public sealed class ReadFileTool : ITool
{
    internal const int MaxChars = 8000;
    internal const int DefaultLines = 400;

    public ToolDefinition Definition { get; } = new(
        Name: "read_file",
        Description: "Read a UTF-8 text file from the current workspace. Path is relative to the "
                   + "workspace root. Reads from 'offset' (1-based line, default 1) for 'limit' "
                   + "lines (default 400); the result says how many lines the file has. To read SEVERAL "
                   + "files, pass 'paths' instead: independent reads in one call rather than one call each.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.None, ParallelRead: true, Kind: ToolKind.Read, FileCoverage: FileCoverageBehavior.Read);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        => FileReadService.InvokeAsync(argumentsJson, ctx, ct, batchOnly: false);

    /// <summary>How many lines a file has, counted the way this tool counts them: one more than its line breaks.</summary>
    internal static int LinesIn(string text) => 1 + text.Count(c => c == '\n');

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path relative to the workspace root." },
        "offset": { "type": "integer", "description": "First line to read, 1-based. Default 1." },
        "limit": { "type": "integer", "description": "How many lines to read. Default 400." },
        "paths": { "type": "array", "items": { "type": "string" }, "description": "Several files to read in ONE call, instead of 'path' - use it whenever the reads do not depend on each other. Each is shown from its start and its end within a shared budget." }
      }
    }
    """;
}
