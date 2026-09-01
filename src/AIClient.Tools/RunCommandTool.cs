namespace AIClient.Tools;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIClient.Core.Permissions;
using AIClient.Core.Tools;

/// <summary>
/// Runs a shell command in the workspace directory (Execute level — and policy keeps it behind an
/// approval by default). Captures stdout/stderr and the exit code.
/// </summary>
public sealed class RunCommandTool : ITool
{
    private const int TimeoutSeconds = 60;
    private const int MaxOutputChars = 4000;

    public ToolDefinition Definition { get; } = new(
        Name: "run_command",
        Description: "Run a shell command in the workspace directory and return its stdout/stderr and exit code. "
                   + "Use for builds, tests, git, etc.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? command;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            command = doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
        }
        catch (JsonException ex)
        {
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(command))
            return ToolResults.Fail("'command' is required.");

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = ctx.WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return ToolResults.Fail($"Command timed out after {TimeoutSeconds}s or was cancelled.");
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not run command: {ex.Message}");
        }

        var combined = stdout.ToString();
        if (stderr.Length > 0)
            combined += "\n[stderr]\n" + stderr;
        combined = combined.Trim();
        if (combined.Length > MaxOutputChars)
            combined = combined[..MaxOutputChars] + "\n… (truncated)";

        var output = $"exit code {process.ExitCode}\n{combined}";
        return ToolResults.Ok(
            output: output,
            metadata: new Dictionary<string, object?> { ["exitCode"] = process.ExitCode });
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "command": { "type": "string", "description": "The shell command to run in the workspace directory." }
      },
      "required": ["command"]
    }
    """;
}
