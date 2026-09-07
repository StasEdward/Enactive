namespace Enactive.Tools;

using System.Diagnostics;
using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Runs a shell command in the workspace directory (Execute level — and policy keeps it behind an
/// approval by default). Captures stdout/stderr and the exit code.
/// </summary>
public sealed class RunCommandTool : ITool
{
    private const int TimeoutSeconds = 60;

    public ToolDefinition Definition { get; } = new(
        Name: "run_command",
        Description: "Run a shell command in the workspace directory and return its stdout/stderr and exit code. "
                   + "Use for builds, tests, git, etc. A non-zero exit code is a FAILURE unless you declared it "
                   + "in 'expectedExitCodes' before running - do that when the exit code is part of the answer "
                   + "you want (a test runner reporting failing tests), never to excuse a command that was "
                   + "supposed to succeed.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? command;
        IReadOnlyCollection<int>? expected;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            command = doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;

            // Read here, used at the very end - so a malformed declaration is refused BEFORE the
            // command runs rather than after it has had its effect.
            if (!ProcessExec.TryReadExpectedExitCodes(doc.RootElement, out expected, out var badCodes))
                return ToolResults.Fail(badCodes!);
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
        // Bounded on purpose - see ProcessExec.CapturedStream. A command's output is not a budget
        // this application should let the command set.
        var stdout = new ProcessExec.CapturedStream();
        var stderr = new ProcessExec.CapturedStream();
        process.OutputDataReceived += (_, e) => stdout.Add(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.Add(e.Data);

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

        return ProcessExec.BuildResult(
            "Command", process.ExitCode, stdout.ToString(), stderr.ToString(), expected, declarable: true);
    }

    private static readonly string Schema = $$"""
    {
      "type": "object",
      "properties": {
        "command": { "type": "string", "description": "The shell command to run in the workspace directory." },
        {{ProcessExec.ExpectedExitCodesSchema}}
      },
      "required": ["command"]
    }
    """;
}
