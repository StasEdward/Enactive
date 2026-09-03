namespace AIClient.Tools;

using System.Text.Json;
using AIClient.Core.Permissions;
using AIClient.Core.Tools;

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
                   + "Pass 'args' as an array of strings (preferred), e.g. [\"status\"] or "
                   + "[\"log\",\"--oneline\",\"-5\"] or [\"commit\",\"-m\",\"message with spaces\"]; a single "
                   + "string also works for simple commands. Do NOT include the leading 'git'.",
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

        return await ProcessExec.RunAsync("git", args, ctx.WorkspaceRoot, TimeoutSeconds, ct);
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "args": {
          "description": "Git arguments without the leading 'git' — an array of strings (preferred) or a single string.",
          "type": ["array", "string"],
          "items": { "type": "string" }
        }
      },
      "required": ["args"]
    }
    """;
}
