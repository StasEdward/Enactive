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
        Description: "Run a docker command and return its stdout/stderr + exit code. Pass 'args' as an "
                   + "array of strings (preferred), e.g. [\"ps\",\"-a\"] or [\"logs\",\"mysql\"] or "
                   + "[\"compose\",\"up\",\"-d\"]; a single string also works. Do NOT include the leading 'docker'.",
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
            return ToolResults.Fail("'args' is required (e.g. [\"ps\",\"-a\"]).");

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
