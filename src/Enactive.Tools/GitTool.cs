namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Runs a git command in the workspace. Arguments are passed as an array (preferred, no quoting
/// issues) or a single string. Git can execute aliases, hooks, filters and external helpers and
/// select other repositories. This is general command execution governed by ShellTools policy,
/// not a workspace sandbox. Execute-level and gated behind an approval by default.
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
                   + "Do NOT include the leading 'git'. Git can run external programs through configuration/hooks "
                   + "and access other repositories; shell execution permissions apply.",
        JsonSchema: Schema, Kind: ToolKind.Command);

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
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (args.Count == 0)
            return ToolResults.Unreadable("'args' is required (e.g. [\"status\"]).");

        // One whole command line in one array element is the mistake these tools attract.
        // Refused with the fix spelled out, because exit code 1 and "is not a git command" is
        // not something a model can act on - and on 2026-09-07 it did not, eighteen times.
        if (ProcessExec.WrongShapeOfArgs("git", args) is { } wrongShape)
            return ToolResults.Unreadable(wrongShape);

        var result = await ProcessExec.RunAsync("git", args, ctx.WorkspaceRoot, TimeoutSeconds, ct);

        // Git refusing its own arguments is not work that failed - it is a line git would not
        // accept, and nothing happened. The shells have said this since 2026-09-20; git could not,
        // because it is started directly with an argument list and none of a shell's wording
        // applies to it. Three runs died on that in one day: a mistyped subcommand corrected a
        // second later, quotes that never parsed, and a JSON array encoded as a string.
        //
        // Read here, in the tool, for the reason ToolResults.NeverRan gives: the tool is the one
        // that knows, because it holds the arguments it sent and is reading git's own fixed wording
        // about them rather than guessing at an error downstream.
        if (!result.Success && ShellOutcome.GitRefusedIt(args, result.Error + Environment.NewLine + result.Output))
            return ToolResults.NeverRan(
                "git would not accept that: " + (result.Error ?? "").Trim()
                + " Nothing was run. Send each argument as its own element of 'args' - "
                + "[\"status\", \"--short\"], not [\"status --short\"] and not a string that "
                + "looks like an array.",
                result.Output);

        // A lookup git answered with "not in that revision" - see ShellOutcome.GitFoundNothing.
        if (!result.Success && ShellOutcome.GitFoundNothing(result.Error + Environment.NewLine + result.Output))
            return ToolResults.NotFound(
                "git answered: that path is not in that revision - it was never committed there. "
                + "That is the answer, not a failure: there is no copy of it in git to get back.",
                result.Output);

        return result;
    }

    private static readonly string Schema = $$"""
    {
      "type": "object",
      "properties": {
        "args": {
          "description": "Git arguments without the leading 'git'. An array with ONE ARGUMENT PER ELEMENT ([\"diff\",\"HEAD\"], not [\"diff HEAD\"]), or a single string that is split on spaces.",
          "type": ["array", "string"],
          "items": { "type": "string" }
        },
        {{ProcessExec.ForceSchema}}
      },
      "required": ["args"]
    }
    """;
}
