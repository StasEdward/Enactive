namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Runs a git command in the workspace. Arguments are passed as an array (preferred, no quoting
/// issues) or a single string; the tool always invokes the real <c>git</c> binary — it cannot run
/// anything else. Execute-level and gated behind an approval by default (it can also mutate history).
/// </summary>
public sealed class GitTool : ITool
{
    private const int TimeoutSeconds = 60;

    public ToolDefinition Definition { get; } = new(
        Name: "git",
        Description: "Run a git command in the workspace and return its stdout/stderr + exit code. "
                   + "ONE ARGUMENT PER ELEMENT: [\"diff\",\"HEAD\"], NOT [\"diff HEAD\"] - a whole command "
                   + "line in one element is passed through as one argument and git will not "
                   + "recognise it. More examples: [\"status\"], [\"log\",\"--oneline\",\"-5\"], "
                   + "[\"commit\",\"-m\",\"message with spaces\"] - a later argument may contain spaces, the "
                   + "subcommand never does. A plain string is also accepted and is split on spaces. "
                   + "Do NOT include the leading 'git'.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        List<string> args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            args = doc.RootElement.TryGetProperty("args", out var a) ? ProcessExec.ParseArgs(a) : new List<string>();
        }
        catch (JsonException ex)
        {
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (args.Count == 0)
            return ToolResults.Fail("'args' is required (e.g. [\"status\"]).");

        // One whole command line in one array element is the mistake these tools attract.
        // Refused with the fix spelled out, because exit code 1 and "is not a git command" is
        // not something a model can act on - and on 2026-09-07 it did not, eighteen times.
        if (ProcessExec.WrongShapeOfArgs("git", args) is { } wrongShape)
            return ToolResults.Fail(wrongShape);

        return await ProcessExec.RunAsync("git", args, ctx.WorkspaceRoot, TimeoutSeconds, ct);
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "args": {
          "description": "Git arguments without the leading 'git'. An array with ONE ARGUMENT PER ELEMENT ([\"diff\",\"HEAD\"], not [\"diff HEAD\"]), or a single string that is split on spaces.",
          "type": ["array", "string"],
          "items": { "type": "string" }
        }
      },
      "required": ["args"]
    }
    """;
}
