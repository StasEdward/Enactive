namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Runs a docker command in the workspace. Arguments are passed as an array (preferred) or a single
/// string; the tool always invokes the real <c>docker</c> binary. Execute-level and gated behind an
/// approval by default (it can start/stop/remove containers).
/// </summary>
public sealed class DockerTool : ITool
{
    private const int TimeoutSeconds = 60;

    public ToolDefinition Definition { get; } = new(
        Name: "docker",
        Description: "Run a docker command and return its stdout/stderr + exit code. "
                   + "ONE ARGUMENT PER ELEMENT: [\"ps\",\"-a\"], NOT [\"ps -a\"] - a whole command line in "
                   + "one element is passed through as one argument and docker will not recognise it. "
                   + "More examples: [\"logs\",\"mysql\"], [\"compose\",\"up\",\"-d\"]. A plain string is also "
                   + "accepted and is split on spaces. Do NOT include the leading 'docker'.",
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
            return ToolResults.Unreadable("'args' is required (e.g. [\"ps\",\"-a\"]).");

        // One whole command line in one array element is the mistake these tools attract.
        // Refused with the fix spelled out, because exit code 1 and "is not a docker command" is
        // not something a model can act on - and on 2026-09-07 it did not, eighteen times.
        if (ProcessExec.WrongShapeOfArgs("docker", args) is { } wrongShape)
            return ToolResults.Unreadable(wrongShape);

        return await ProcessExec.RunAsync("docker", args, ctx.WorkspaceRoot, TimeoutSeconds, ct);
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "args": {
          "description": "Docker arguments without the leading 'docker' — an array of strings (preferred) or a single string.",
          "type": ["array", "string"],
          "items": { "type": "string" }
        }
      },
      "required": ["args"]
    }
    """;
}
